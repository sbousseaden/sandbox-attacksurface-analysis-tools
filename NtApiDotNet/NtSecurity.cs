﻿//  Copyright 2016 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using NtApiDotNet.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NtApiDotNet
{
    /// <summary>
    /// Static class to access NT security manager routines.
    /// </summary>
    public static class NtSecurity
    {
        /// <summary>
        /// Looks up the account name of a SID. 
        /// </summary>
        /// <param name="sid">The SID to lookup</param>
        /// <returns>The name, or null if the lookup failed</returns>
        public static string LookupAccountSid(Sid sid)
        {
            using (SafeSidBufferHandle sid_buffer = sid.ToSafeBuffer())
            {
                StringBuilder name = new StringBuilder(1024);
                int length = name.Capacity;
                StringBuilder domain = new StringBuilder(1024);
                int domain_length = domain.Capacity;
                if (!Win32NativeMethods.LookupAccountSid(null, sid_buffer, name, 
                    ref length, domain, ref domain_length, out SidNameUse name_use))
                {
                    return null;
                }

                if (domain_length == 0)
                {
                    return name.ToString();
                }
                else
                {
                    return $@"{domain}\{name}";
                }
            }
        }

        private static Dictionary<Sid, string> _known_capabilities = null;

        private static Dictionary<Sid, string> GetKnownCapabilitySids()
        {
            if (_known_capabilities == null)
            {
                Dictionary<Sid, string> known_capabilities = new Dictionary<Sid, string>();
                try
                {
                    foreach (string name in SecurityCapabilities.KnownCapabilityNames)
                    {
                        GetCapabilitySids(name, out Sid capability_sid, out Sid capability_group_sid);
                        known_capabilities.Add(capability_sid, name);
                        known_capabilities.Add(capability_group_sid, name);
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // Catch here in case the RtlDeriveCapabilitySid function isn't supported.
                }
                _known_capabilities = known_capabilities;
            }
            return _known_capabilities;
        }

        /// <summary>
        /// Looks up a capability SID to see if it's already known.
        /// </summary>
        /// <param name="sid">The capability SID to lookup</param>
        /// <returns>The name of the capability, null if not found.</returns>
        public static string LookupKnownCapabilityName(Sid sid)
        {
            var known_caps = GetKnownCapabilitySids();
            if (known_caps.ContainsKey(sid))
            {
                return known_caps[sid];
            }
            return null;
        }

        /// <summary>
        /// Lookup a SID from a username.
        /// </summary>
        /// <param name="username">The username, can be in the form domain\account.</param>
        /// <returns>The Security Identifier</returns>
        /// <exception cref="NtException">Thrown if account cannot be found.</exception>
        public static Sid LookupAccountName(string username)
        {
            int sid_length = 0;
            int domain_length = 0;
            if (!Win32NativeMethods.LookupAccountName(null, username, SafeHGlobalBuffer.Null, ref sid_length,
                SafeHGlobalBuffer.Null, ref domain_length, out SidNameUse name))
            {
                if (sid_length <= 0)
                {
                    throw new NtException(NtStatus.STATUS_INVALID_USER_PRINCIPAL_NAME);
                }
            }

            using (SafeHGlobalBuffer buffer = new SafeHGlobalBuffer(sid_length), domain = new SafeHGlobalBuffer(domain_length * 2))
            {
                if (!Win32NativeMethods.LookupAccountName(null, username, buffer, ref sid_length, domain, ref domain_length, out name))
                {
                    throw new NtException(NtStatus.STATUS_INVALID_USER_PRINCIPAL_NAME);
                }

                return new Sid(buffer);
            }
        }

        /// <summary>
        /// Lookup the name of a process trust SID.
        /// </summary>
        /// <param name="trust_sid">The trust sid to lookup.</param>
        /// <returns>The name of the trust sid. null if not found.</returns>
        /// <exception cref="ArgumentException">Thrown if trust_sid is not a trust sid.</exception>
        public static string LookupProcessTrustName(Sid trust_sid)
        {
            if (!IsProcessTrustSid(trust_sid))
            {
                throw new ArgumentException("Must pass a process trust sid to lookup", "trust_sid");
            }

            if (trust_sid.SubAuthorities.Count != 2)
            {
                return null;
            }

            string protection_type;
            switch (trust_sid.SubAuthorities[0])
            {
                case 0:
                    protection_type = "None";
                    break;
                case 512:
                    protection_type = "ProtectedLight";
                    break;
                case 1024:
                    protection_type = "Protected";
                    break;
                default:
                    protection_type = $"Protected-{trust_sid.SubAuthorities[0]}";
                    break;
            }

            string protection_level;
            switch (trust_sid.SubAuthorities[1])
            {
                case 0:
                    protection_level = "None";
                    break;
                case 1024:
                    protection_level = "Authenticode";
                    break;
                case 1536:
                    protection_level = "AntiMalware";
                    break;
                case 2048:
                    protection_level = "App";
                    break;
                case 4096:
                    protection_level = "Windows";
                    break;
                case 8192:
                    protection_level = "WinTcb";
                    break;
                default:
                    protection_level = trust_sid.SubAuthorities[1].ToString();
                    break;
            }

            return $"{protection_type}-{protection_level}";
        }

        private static string ReadMoniker(NtKey rootkey, Sid sid)
        {
            PackageSidType sid_type = GetPackageSidType(sid);
            Sid child_sid = null;
            if (sid_type == PackageSidType.Child)
            {
                child_sid = sid;
                sid = GetPackageSidParent(sid);
            }

            string path = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\{sid}";
            if (child_sid != null)
            {
                path = $@"{path}\Children\{child_sid}";
            }

            using (ObjectAttributes obj_attr = new ObjectAttributes(path, AttributeFlags.CaseInsensitive, rootkey))
            {
                using (var key = NtKey.Open(obj_attr, KeyAccessRights.QueryValue, KeyCreateOptions.NonVolatile, false))
                {
                    if (key.IsSuccess)
                    {
                        var moniker = key.Result.QueryValue("Moniker", false);
                        if (!moniker.IsSuccess)
                        {
                            return null;
                        }

                        if (child_sid == null)
                        {
                            return moniker.Result.ToString().TrimEnd('\0');
                        }

                        var parent_moniker = key.Result.QueryValue("ParentMoniker", false);
                        string parent_moniker_string;
                        if (parent_moniker.IsSuccess)
                        {
                            parent_moniker_string = parent_moniker.Result.ToString();
                        }
                        else
                        {
                            parent_moniker_string = ReadMoniker(rootkey, sid) ?? String.Empty;
                        }

                        return $"{parent_moniker_string.TrimEnd('\0')}/{moniker.Result.ToString().TrimEnd('\0')}";
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Try and lookup the moniker associated with a package sid.
        /// </summary>
        /// <param name="sid">The package sid.</param>
        /// <returns>Returns the moniker name. If not found returns null.</returns>
        /// <exception cref="ArgumentException">Thrown if SID is not a package sid.</exception>
        public static string LookupPackageName(Sid sid)
        {
            if (!IsPackageSid(sid))
            {
                throw new ArgumentException("Sid not a package sid", "sid");
            }

            string ret = null;
            try
            {
                using (NtKey key = NtKey.GetCurrentUserKey())
                {
                    ret = ReadMoniker(key, sid);
                }
            }
            catch (NtException)
            {
            }

            if (ret == null)
            {
                try
                {
                    using (NtKey key = NtKey.GetMachineKey())
                    {
                        ret = ReadMoniker(key, sid);
                    }
                }
                catch (NtException)
                {
                }
            }

            return ret;
        }

        private static Dictionary<Sid, string> _device_capabilities;

        private static Sid GuidToCapabilitySid(Guid g)
        {
            byte[] guid_buffer = g.ToByteArray();
            List<uint> subauthorities = new List<uint>
            {
                3
            };
            for (int i = 0; i < 4; ++i)
            {
                subauthorities.Add(BitConverter.ToUInt32(guid_buffer, i * 4));
            }
            return new Sid(SecurityAuthority.Package, subauthorities.ToArray());
        }

        private static Dictionary<Sid, string> GetDeviceCapabilities()
        {
            if (_device_capabilities != null)
            {
                return _device_capabilities;
            }

            var device_capabilities = new Dictionary<Sid, string>();

            try
            {
                using (var base_key = NtKey.Open(@"\Registry\Machine\SOFTWARE\Microsoft\Windows\CurrentVersion\DeviceAccess\CapabilityMappings", null, KeyAccessRights.EnumerateSubKeys))
                {
                    using (var key_list = base_key.QueryAccessibleKeys(KeyAccessRights.EnumerateSubKeys).ToDisposableList())
                    {
                        foreach (var key in key_list)
                        {
                            foreach (var guid in key.QueryKeys())
                            {
                                if (Guid.TryParse(guid, out Guid g))
                                {
                                    Sid sid = GuidToCapabilitySid(g);
                                    if (!device_capabilities.ContainsKey(sid))
                                    {
                                        device_capabilities[sid] = key.Name;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (NtException)
            {
            }
            
            _device_capabilities = device_capabilities;
            return _device_capabilities;
        }

        /// <summary>
        /// Lookup a device capability SID name if known.
        /// </summary>
        /// <param name="sid">The SID to lookup.</param>
        /// <returns>Returns the device capability name. If not found returns null.</returns>
        /// <exception cref="ArgumentException">Thrown if SID is not a package sid.</exception>
        public static string LookupDeviceCapabilityName(Sid sid)
        {
            if (!IsCapabilitySid(sid))
            {
                throw new ArgumentException("Sid not a capability sid", "sid");
            }

            var device_capabilities = GetDeviceCapabilities();
            if (device_capabilities.ContainsKey(sid))
            {
                return device_capabilities[sid];
            }
            return null;
        }

        /// <summary>
        /// Convert a security descriptor to SDDL string
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="security_information">Indicates what parts of the security descriptor to include</param>
        /// <returns>The SDDL string</returns>
        /// <exception cref="NtException">Thrown if cannot convert to a SDDL string.</exception>
        public static string SecurityDescriptorToSddl(byte[] sd, SecurityInformation security_information)
        {
            if (!Win32NativeMethods.ConvertSecurityDescriptorToStringSecurityDescriptor(sd,
                1, security_information, out SafeLocalAllocHandle handle, out int return_length))
            {
                throw new NtException(NtObjectUtils.MapDosErrorToStatus());
            }

            using (handle)
            {
                return Marshal.PtrToStringUni(handle.DangerousGetHandle());
            }
        }

        /// <summary>
        /// Convert an SDDL string to a binary security descriptor
        /// </summary>
        /// <param name="sddl">The SDDL string</param>
        /// <returns>The binary security descriptor</returns>
        /// <exception cref="NtException">Thrown if cannot convert from a SDDL string.</exception>
        public static byte[] SddlToSecurityDescriptor(string sddl)
        {
            if (!Win32NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, 
                out SafeLocalAllocHandle handle, out int return_length))
            {
                throw new NtException(NtObjectUtils.MapDosErrorToStatus());
            }

            using (handle)
            {
                byte[] ret = new byte[return_length];
                Marshal.Copy(handle.DangerousGetHandle(), ret, 0, return_length);
                return ret;
            }
        }

        /// <summary>
        /// Convert an SDDL SID string to a Sid
        /// </summary>
        /// <param name="sddl">The SDDL SID string</param>
        /// <returns>The converted Sid</returns>
        /// <exception cref="NtException">Thrown if cannot convert from a SDDL string.</exception>
        public static Sid SidFromSddl(string sddl)
        {
            if (!Win32NativeMethods.ConvertStringSidToSid(sddl, out SafeLocalAllocHandle handle))
            {
                throw new NtException(NtObjectUtils.MapDosErrorToStatus());
            }
            using (handle)
            {
                return new Sid(handle.DangerousGetHandle());
            }
        }

        private static NtToken DuplicateForAccessCheck(NtToken token)
        {
            if (token.IsPseudoToken)
            {
                // This is a pseudo token, pass along as no need to duplicate.
                return token;
            }

            if (token.TokenType == TokenType.Primary)
            {
                return token.DuplicateToken(TokenType.Impersonation, SecurityImpersonationLevel.Identification, TokenAccessRights.Query);
            }
            else if (!token.IsAccessGranted(TokenAccessRights.Query))
            {
                return token.Duplicate(TokenAccessRights.Query);
            }
            else
            {
                // If we've got query access rights already just create a shallow clone.
                return token.ShallowClone();
            }
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="access_rights">The set of access rights to check against</param>
        /// <param name="principal">An optional principal SID used to replace the SELF SID in a security descriptor.</param>
        /// <param name="generic_mapping">The type specific generic mapping (get from corresponding NtType entry).</param>
        /// <returns>The allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetAllowedAccess(SecurityDescriptor sd, NtToken token,
            AccessMask access_rights, Sid principal, GenericMapping generic_mapping)
        {
            if (sd == null)
            {
                throw new ArgumentNullException("sd");
            }

            if (token == null)
            {
                throw new ArgumentNullException("token");
            }

            if (access_rights.IsEmpty)
            {
                return AccessMask.Empty;
            }

            using (SafeBuffer sd_buffer = sd.ToSafeBuffer())
            {
                using (NtToken imp_token = DuplicateForAccessCheck(token))
                {
                    using (var privs = new SafePrivilegeSetBuffer())
                    {
                        int buffer_length = privs.Length;

                        using (var self_sid = principal != null ? principal.ToSafeBuffer() : SafeSidBufferHandle.Null)
                        {
                            NtSystemCalls.NtAccessCheckByType(sd_buffer, self_sid, imp_token.Handle, access_rights,
                                SafeHGlobalBuffer.Null, 0, ref generic_mapping, privs,
                                ref buffer_length, out AccessMask granted_access, out NtStatus result_status).ToNtException();
                            if (result_status.IsSuccess())
                            {
                                return granted_access;
                            }
                            return AccessMask.Empty;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="access_rights">The set of access rights to check against</param>
        /// <param name="generic_mapping">The type specific generic mapping (get from corresponding NtType entry).</param>
        /// <returns>The allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetAllowedAccess(SecurityDescriptor sd, NtToken token,
            AccessMask access_rights, GenericMapping generic_mapping)
        {
            return GetAllowedAccess
                (sd, token, access_rights, null, generic_mapping);
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the maximum allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="generic_mapping">The type specific generic mapping (get from corresponding NtType entry).</param>
        /// <returns>The maximum allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetMaximumAccess(SecurityDescriptor sd, NtToken token, GenericMapping generic_mapping)
        {
            return GetAllowedAccess(sd, token, GenericAccessRights.MaximumAllowed, generic_mapping);
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the maximum allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="principal">An optional principal SID used to replace the SELF SID in a security descriptor.</param>
        /// <param name="generic_mapping">The type specific generic mapping (get from corresponding NtType entry).</param>
        /// <returns>The maximum allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetMaximumAccess(SecurityDescriptor sd, NtToken token, Sid principal, GenericMapping generic_mapping)
        {
            return GetAllowedAccess(sd, token, GenericAccessRights.MaximumAllowed, principal, generic_mapping);
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="access_rights">The set of access rights to check against</param>
        /// <param name="type">The type used to determine generic access mapping..</param>
        /// <returns>The allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetAllowedAccess(NtToken token, NtType type, AccessMask access_rights, byte[] sd)
        {
            if (sd == null || sd.Length == 0)
            {
                return AccessMask.Empty;
            }

            return GetAllowedAccess(new SecurityDescriptor(sd), token, access_rights, type.GenericMapping);
        }

        /// <summary>
        /// Do an access check between a security descriptor and a token to determine the maximum allowed access.
        /// </summary>
        /// <param name="sd">The security descriptor</param>
        /// <param name="token">The access token.</param>
        /// <param name="type">The type used to determine generic access mapping..</param>
        /// <returns>The allowed access mask as a unsigned integer.</returns>
        /// <exception cref="NtException">Thrown if an error occurred in the access check.</exception>
        public static AccessMask GetMaximumAccess(NtToken token, NtType type, byte[] sd)
        {
            return GetAllowedAccess(token, type, GenericAccessRights.MaximumAllowed, sd);
        }

        /// <summary>
        /// Get a security descriptor from a named object.
        /// </summary>
        /// <param name="name">The path to the resource (such as \BaseNamedObejct\ABC)</param>
        /// <param name="type">The type of resource, can be null to get the method to try and discover the correct type.</param>
        /// <returns>The named resource security descriptor.</returns>
        /// <exception cref="NtException">Thrown if an error occurred opening the object.</exception>
        /// <exception cref="ArgumentException">Thrown if type of resource couldn't be found.</exception>
        public static SecurityDescriptor FromNamedObject(string name, string type)
        {
            try
            {
                using (NtObject obj = NtObject.OpenWithType(type, name, null, GenericAccessRights.ReadControl))
                {
                    return obj.SecurityDescriptor;
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Get a SID for a specific mandatory integrity level.
        /// </summary>
        /// <param name="level">The mandatory integrity level.</param>
        /// <returns>The integrity SID</returns>
        public static Sid GetIntegritySidRaw(int level)
        {
            return new Sid(SecurityAuthority.Label, (uint)level);
        }

        /// <summary>
        /// Get a SID for a specific mandatory integrity level.
        /// </summary>
        /// <param name="level">The mandatory integrity level.</param>
        /// <returns>The integrity SID</returns>
        public static Sid GetIntegritySid(TokenIntegrityLevel level)
        {
            return GetIntegritySidRaw((int)level);
        }

        /// <summary>
        /// Checks if a SID is an integrity level SID
        /// </summary>
        /// <param name="sid">The SID to check</param>
        /// <returns>True if an integrity SID</returns>
        public static bool IsIntegritySid(Sid sid)
        {
            return GetIntegritySid(TokenIntegrityLevel.Untrusted).EqualPrefix(sid);
        }

        /// <summary>
        /// Get the integrity level from an integrity SID
        /// </summary>
        /// <param name="sid">The integrity SID</param>
        /// <returns>The token integrity level.</returns>
        public static TokenIntegrityLevel GetIntegrityLevel(Sid sid)
        {
            if (!IsIntegritySid(sid))
            {
                throw new ArgumentException("Must specify an integrity SID", "sid");
            }
            return (TokenIntegrityLevel)sid.SubAuthorities[sid.SubAuthorities.Count - 1];
        }

        /// <summary>
        /// Gets the SID for a service name.
        /// </summary>
        /// <param name="service_name">The service name.</param>
        /// <returns>The service SID.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public static Sid GetServiceSid(string service_name)
        {
            using (SafeHGlobalBuffer buffer = new SafeHGlobalBuffer(1024))
            {
                int sid_length = buffer.Length;
                NtRtl.RtlCreateServiceSid(new UnicodeString(service_name), buffer, ref sid_length).ToNtException();
                return new Sid(buffer);
            }
        }

        /// <summary>
        /// Checks if a SID is a service SID.
        /// </summary>
        /// <param name="sid">The sid to check.</param>
        /// <returns>True if a service sid.</returns>
        public static bool IsServiceSid(Sid sid)
        {
            return sid.Authority.IsAuthority(SecurityAuthority.Nt) && sid.SubAuthorities.Count > 0 && sid.SubAuthorities[0] == 80;
        }

        /// <summary>
        /// Checks if a SID is a process trust SID.
        /// </summary>
        /// <param name="sid">The sid to check.</param>
        /// <returns>True if a process trust sid.</returns>
        public static bool IsProcessTrustSid(Sid sid)
        {
            return sid.Authority.IsAuthority(SecurityAuthority.ProcessTrust);
        }

        /// <summary>
        /// Checks if a SID is a capability SID.
        /// </summary>
        /// <param name="sid">The sid to check.</param>
        /// <returns>True if a capability sid.</returns>
        public static bool IsCapabilitySid(Sid sid)
        {
            return sid.Authority.IsAuthority(SecurityAuthority.Package) &&
                sid.SubAuthorities.Count > 0 &&
                (sid.SubAuthorities[0] == 3);
        }

        /// <summary>
        /// Checks if a SID is a capbility group SID.
        /// </summary>
        /// <param name="sid">The sid to check.</param>
        /// <returns>True if a capability group sid.</returns>
        public static bool IsCapabilityGroupSid(Sid sid)
        {
            return sid.Authority.IsAuthority(SecurityAuthority.Nt) && 
                sid.SubAuthorities.Count == 9 &&
                sid.SubAuthorities[0] == 32;
        }

        private static void GetCapabilitySids(string capability_name, out Sid capability_sid, out Sid capability_group_sid)
        {
            using (SafeHGlobalBuffer cap_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize),
                    cap_group_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize))
            {
                NtRtl.RtlDeriveCapabilitySidsFromName(
                    new UnicodeString(capability_name),
                    cap_group_sid, cap_sid).ToNtException();
                capability_sid = new Sid(cap_sid);
                capability_group_sid = new Sid(cap_group_sid);
            }
        }

        /// <summary>
        /// Get a capability sid by name.
        /// </summary>
        /// <param name="capability_name">The name of the capability.</param>
        /// <returns>The capability SID.</returns>
        public static Sid GetCapabilitySid(string capability_name)
        {
            using (SafeHGlobalBuffer cap_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize), 
                cap_group_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize))
            {
                NtRtl.RtlDeriveCapabilitySidsFromName(
                    new UnicodeString(capability_name),
                    cap_group_sid, cap_sid).ToNtException();
                return new Sid(cap_sid);
            }
        }

        /// <summary>
        /// Get a capability group sid by name.
        /// </summary>
        /// <param name="capability_name">The name of the capability.</param>
        /// <returns>The capability SID.</returns>
        public static Sid GetCapabilityGroupSid(string capability_name)
        {
            using (SafeHGlobalBuffer cap_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize),
                cap_group_sid = new SafeHGlobalBuffer(Sid.MaximumSidSize))
            {
                NtRtl.RtlDeriveCapabilitySidsFromName(
                    new UnicodeString(capability_name),
                    cap_group_sid, cap_sid).ToNtException();
                return new Sid(cap_group_sid);
            }
        }

        /// <summary>
        /// Get the type of package sid.
        /// </summary>
        /// <param name="sid">The sid to get type.</param>
        /// <returns>The package sid type, Unknown if invalid.</returns>
        public static PackageSidType GetPackageSidType(Sid sid)
        {
            if (IsPackageSid(sid))
            {
                return sid.SubAuthorities.Count == 8 ? PackageSidType.Parent : PackageSidType.Child;
            }
            return PackageSidType.Unknown;
        }

        /// <summary>
        /// Checks if a SID is a valid package SID.
        /// </summary>
        /// <param name="sid">The sid to check.</param>
        /// <returns>True if a capability sid.</returns>
        public static bool IsPackageSid(Sid sid)
        {
            return sid.Authority.IsAuthority(SecurityAuthority.Package) &&
                (sid.SubAuthorities.Count == 8 || sid.SubAuthorities.Count == 12) &&
                (sid.SubAuthorities[0] == 2);
        }

        /// <summary>
        /// Get the parent package SID for a child package SID.
        /// </summary>
        /// <param name="sid">The child package SID.</param>
        /// <returns>The parent package SID.</returns>
        /// <exception cref="ArgumentException">Thrown if sid not a child package SID.</exception>
        public static Sid GetPackageSidParent(Sid sid)
        {
            if (GetPackageSidType(sid) != PackageSidType.Child)
            {
                throw new ArgumentException("Package sid not a child sid");
            }

            return new Sid(sid.Authority, sid.SubAuthorities.Take(8).ToArray());
        }

        private static Regex ConditionalAceRegex = new Regex(@"^D:\(XA;;;;;WD;\((.+)\)\)$");

        /// <summary>
        /// Converts conditional ACE data to an SDDL string
        /// </summary>
        /// <param name="conditional_data">The conditional application data.</param>
        /// <returns>The conditional ACE string.</returns>
        public static string ConditionalAceToString(byte[] conditional_data)
        {
            SecurityDescriptor sd = new SecurityDescriptor
            {
                Dacl = new Acl
                {
                    NullAcl = false
                }
            };
            sd.Dacl.Add(new Ace(AceType.AllowedCallback, AceFlags.None, 0, KnownSids.World) { ApplicationData = conditional_data });
            var matches = ConditionalAceRegex.Match(sd.ToSddl());

            if (!matches.Success || matches.Groups.Count != 2)
            {
                throw new ArgumentException("Invalid condition data");
            }
            return matches.Groups[1].Value;
        }

        /// <summary>
        /// Converts a condition in SDDL format to an ACE application data.
        /// </summary>
        /// <param name="condition_sddl">The condition in SDDL format.</param>
        /// <returns>The condition in ACE application data format.</returns>
        public static byte[] StringToConditionalAce(string condition_sddl)
        {
            SecurityDescriptor sd = new SecurityDescriptor($"D:(XA;;;;;WD;({condition_sddl}))");
            return sd.Dacl[0].ApplicationData;
        }

        /// <summary>
        /// Get the cached signing level for a file.
        /// </summary>
        /// <param name="handle">The handle to the file to query.</param>
        /// <returns>The cached signing level.</returns>
        public static CachedSigningLevel GetCachedSigningLevel(SafeKernelObjectHandle handle)
        {
            byte[] thumb_print = new byte[0x68];
            int thumb_print_size = thumb_print.Length;

            NtSystemCalls.NtGetCachedSigningLevel(handle, out int flags,
                out SigningLevel signing_level, thumb_print, ref thumb_print_size, out HashAlgorithm thumb_print_algo).ToNtException();
            Array.Resize(ref thumb_print, thumb_print_size);
            return new CachedSigningLevel(flags, signing_level, thumb_print, thumb_print_algo);
        }

        private static CachedSigningLevelEaBuffer ReadCachedSigningLevelVersion1(BinaryReader reader)
        {
            int version2 = reader.ReadInt16();
            int flags = reader.ReadInt32();
            int policy = reader.ReadInt32();
            long last_blacklist_time = reader.ReadInt64();
            int sequence = reader.ReadInt32();
            byte[] thumbprint = reader.ReadAllBytes(64);
            int thumbprint_size = reader.ReadInt32();
            Array.Resize(ref thumbprint, thumbprint_size);
            HashAlgorithm thumbprint_algo = (HashAlgorithm)reader.ReadInt32();
            byte[] hash = reader.ReadAllBytes(64);
            int hash_size = reader.ReadInt32();
            Array.Resize(ref hash, hash_size);
            HashAlgorithm hash_algo = (HashAlgorithm)reader.ReadInt32();
            long usn = reader.ReadInt64();
           
            return new CachedSigningLevelEaBuffer(version2, flags, (SigningLevel)policy, usn,
                last_blacklist_time, sequence, thumbprint, thumbprint_algo, hash, hash_algo);
        }

        private static CachedSigningLevelEaBufferV2 ReadCachedSigningLevelVersion2(BinaryReader reader)
        {
            int version2 = reader.ReadInt16();
            int flags = reader.ReadInt32();
            int policy = reader.ReadInt32();
            long last_blacklist_time = reader.ReadInt64();
            long last_timestamp = reader.ReadInt64();
            int thumbprint_size = reader.ReadInt32();
            HashAlgorithm thumbprint_algo = (HashAlgorithm) reader.ReadInt32();
            int hash_size = reader.ReadInt32();
            HashAlgorithm hash_algo = (HashAlgorithm) reader.ReadInt32();
            long usn = reader.ReadInt64();
            byte[] thumbprint = reader.ReadAllBytes(thumbprint_size);
            byte[] hash = reader.ReadAllBytes(hash_size);
            
            return new CachedSigningLevelEaBufferV2(version2, flags, (SigningLevel)policy, usn,
                last_blacklist_time, last_timestamp, thumbprint, thumbprint_algo, hash, hash_algo);
        }

        private static CachedSigningLevelEaBufferV3 ReadCachedSigningLevelVersion3(BinaryReader reader)
        {
            int version2 = reader.ReadByte();
            int policy = reader.ReadByte();
            long usn = reader.ReadInt64();
            long last_blacklist_time = reader.ReadInt64();
            int flags = reader.ReadInt32();
            int extra_size = reader.ReadUInt16();
            long end_size = reader.BaseStream.Position + extra_size;
            List<CachedSigningLevelBlob> extra_data = new List<CachedSigningLevelBlob>();
            HashCachedSigningLevelBlob thumbprint = null;
            while (reader.BaseStream.Position < end_size)
            {
                CachedSigningLevelBlob blob = CachedSigningLevelBlob.ReadBlob(reader);
                if (blob.BlobType == CachedSigningLevelBlobType.SignerHash)
                {
                    thumbprint = (HashCachedSigningLevelBlob)blob;
                }
                extra_data.Add(blob);
            }

            return new CachedSigningLevelEaBufferV3(version2, flags, (SigningLevel)policy, usn,
                last_blacklist_time, extra_data.AsReadOnly(), thumbprint);
        }

        /// <summary>
        /// Get the cached singing level from the raw EA buffer.
        /// </summary>
        /// <param name="ea">The EA buffer to read the cached signing level from.</param>
        /// <returns>The cached signing level.</returns>
        /// <exception cref="NtException">Throw on error.</exception>
        public static CachedSigningLevel GetCachedSigningLevelFromEa(EaBuffer ea)
        {
            EaBufferEntry buffer = ea.GetEntry("$KERNEL.PURGE.ESBCACHE");
            if (buffer == null)
            {
                NtStatus.STATUS_OBJECT_NAME_NOT_FOUND.ToNtException();
            }

            BinaryReader reader = new BinaryReader(new MemoryStream(buffer.Data));
            int total_size = reader.ReadInt32();
            int version = reader.ReadInt16();
            switch (version)
            {
                case 1:
                    return ReadCachedSigningLevelVersion1(reader);
                case 2:
                    return ReadCachedSigningLevelVersion2(reader);
                case 3:
                    return ReadCachedSigningLevelVersion3(reader);
                default:
                    throw new ArgumentException($"Unsupported cached signing level buffer version {version}");
            }
        }

        /// <summary>
        /// Set the cached signing level for a file.
        /// </summary>
        /// <param name="handle">The handle to the file to set the cache on.</param>
        /// <param name="flags">Flags to set for the cache.</param>
        /// <param name="signing_level">The signing level to cache</param>
        /// <param name="source_files">A list of source file for the cache.</param>
        /// <param name="catalog_path">Optional directory path to look for catalog files.</param>
        public static void SetCachedSigningLevel(SafeKernelObjectHandle handle, 
                                                 int flags, SigningLevel signing_level,
                                                 IEnumerable<SafeKernelObjectHandle> source_files,
                                                 string catalog_path)
        {
            IntPtr[] handles = source_files?.Select(f => f.DangerousGetHandle()).ToArray();
            int handles_count = handles == null ? 0 : handles.Length;
            if (catalog_path != null)
            {
                CachedSigningLevelInformation info = new CachedSigningLevelInformation(catalog_path);
                NtSystemCalls.NtSetCachedSigningLevel2(flags, signing_level, handles, handles_count, handle, info).ToNtException();
            }
            else
            {
                NtSystemCalls.NtSetCachedSigningLevel(flags, signing_level, handles, handles_count, handle).ToNtException();
            }
        }

        private static string UpperCaseString(string name)
        {
            StringBuilder result = new StringBuilder(name);
            if (result.Length > 0)
            {
                result[0] = char.ToUpper(result[0]);
            }
            return result.ToString();
        }

        private static string MakeFakeCapabilityName(string name, bool group)
        {
            List<string> parts = new List<string>();
            if (name.Contains("_"))
            {
                parts.Add(name);
            }
            else
            {
                int start = 0;
                int index = 1;
                while (index < name.Length)
                {
                    if (Char.IsUpper(name[index]))
                    {
                        parts.Add(name.Substring(start, index - start));
                        start = index;
                    }
                    index++;
                }

                parts.Add(name.Substring(start));
                parts[0] = UpperCaseString(parts[0]);
            }

            return $@"NAMED CAPABILITIES{(group ? " GROUP":"")}\{String.Join(" ", parts)}";
        }

        private static SidName GetNameForSidInternal(Sid sid)
        {
            string name = LookupAccountSid(sid);
            if (name != null)
            {
                return new SidName(name, SidNameSource.Account);
            }

            if (IsCapabilitySid(sid))
            {
                // See if there's a known SID with this name.
                name = LookupKnownCapabilityName(sid);
                if (name == null)
                {
                    switch (sid.SubAuthorities.Count)
                    {
                        case 8:
                            uint[] sub_authorities = sid.SubAuthorities.ToArray();
                            // Convert to a package SID.
                            sub_authorities[0] = 2;
                            name = LookupPackageName(new Sid(sid.Authority, sub_authorities));
                            break;
                        case 5:
                            name = LookupDeviceCapabilityName(sid);
                            break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new SidName(MakeFakeCapabilityName(name, false), SidNameSource.Capability);
                }
            }
            else if (IsCapabilityGroupSid(sid))
            {
                name = LookupKnownCapabilityName(sid);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new SidName(MakeFakeCapabilityName(name, true), SidNameSource.Capability);
                }
            }
            else if (IsPackageSid(sid))
            {
                name = LookupPackageName(sid);
                if (name != null)
                {
                    return new SidName(name, SidNameSource.Package);
                }
            }
            else if (IsProcessTrustSid(sid))
            {
                name = LookupProcessTrustName(sid);
                if (name != null)
                {
                    return new SidName($@"TRUST LEVEL\{name}", SidNameSource.ProcessTrust);
                }
            }

            return new SidName(sid.ToString(), SidNameSource.Sddl);
        }

        private static ConcurrentDictionary<Sid, SidName> _cached_names = new ConcurrentDictionary<Sid, SidName>();

        /// <summary>
        /// Get readable name for a SID, if known. This covers sources of names such as LSASS lookup, capability names and package names.
        /// </summary>
        /// <param name="sid">The SID to lookup.</param>
        /// <param name="bypass_cache">True to bypass the internal cache and get the current name.</param>
        /// <returns>The name for the SID. Returns the SDDL form if no other name is known.</returns>
        public static SidName GetNameForSid(Sid sid, bool bypass_cache)
        {
            if (bypass_cache)
            {
                return GetNameForSidInternal(sid);
            }
            return _cached_names.GetOrAdd(sid, s => GetNameForSidInternal(sid));
        }

        /// <summary>
        /// Get readable name for a SID, if known. This covers sources of names such as LSASS lookup, capability names and package names.
        /// </summary>
        /// <param name="sid">The SID to lookup.</param>
        /// <returns>The name for the SID. Returns the SDDL form if no other name is known.</returns>
        /// <remarks>This function will cache name lookups, this means the name might not reflect what's currently in LSASS if it's been changed.</remarks>
        public static SidName GetNameForSid(Sid sid)
        {
            return GetNameForSid(sid, false);
        }

        /// <summary>
        /// Clear the SID name cache.
        /// </summary>
        public static void ClearSidNameCache()
        {
            _cached_names.Clear();
        }
    }
}
