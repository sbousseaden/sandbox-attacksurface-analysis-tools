﻿//  Copyright 2017 Google Inc. All Rights Reserved.
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

using NtApiDotNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;

namespace NtObjectManager
{
    /// <summary>
    ///<para type="description">General Access check result.</para>
    /// </summary>
    public class AccessCheckResult
    {
        /// <summary>
        /// The name of the object which was accessed (depends on the type).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Name of the type accessed.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Granted access.
        /// </summary>
        public AccessMask GrantedAccess { get; }

        /// <summary>
        /// Get granted access as a type specific string
        /// </summary>
        public string GrantedAccessString { get; }

        /// <summary>
        /// Get granted access as generic access string.
        /// </summary>
        public string GrantedGenericAccessString { get; }

        /// <summary>
        /// The generic mapping associated with this type.
        /// </summary>
        public GenericMapping GenericMapping { get; }

        /// <summary>
        /// The security descriptor associated with this access check.
        /// </summary>
        public string SecurityDescriptor { get; }

        /// <summary>
        /// Information the token used in the access check.
        /// </summary>
        public TokenInformation TokenInfo { get; }

        /// <summary>
        /// Was read access granted?
        /// </summary>
        public bool IsRead { get; }

        /// <summary>
        /// Was write access granted?
        /// </summary>
        public bool IsWrite { get; }

        /// <summary>
        /// Was execute access granted?
        /// </summary>
        public bool IsExecute { get; }

        /// <summary>
        /// Was all access granted?
        /// </summary>
        public bool IsAll { get; }

        /// <summary>
        /// Is the resource being access a directory.
        /// </summary>
        public bool IsDirectory { get; }

        /// <summary>
        /// Unique key for access check result (based on TokenId)
        /// </summary>
        public long TokenId { get; }

        internal AccessCheckResult(string name, string type_name, AccessMask granted_access,
            GenericMapping generic_mapping, string sddl, Type enum_type, bool is_directory, TokenInformation token_info)
        {
            Name = name;
            TypeName = type_name;
            GrantedAccess = granted_access;
            GenericMapping = generic_mapping;
            TokenInfo = token_info;
            SecurityDescriptor = sddl;
            IsRead = generic_mapping.HasRead(granted_access);
            IsWrite = generic_mapping.HasWrite(granted_access);
            IsExecute = generic_mapping.HasExecute(granted_access);
            IsAll = generic_mapping.HasAll(granted_access);
            GrantedAccessString = NtObjectUtils.GrantedAccessAsString(granted_access, generic_mapping, enum_type, false);
            GrantedGenericAccessString = NtObjectUtils.GrantedAccessAsString(granted_access, generic_mapping, enum_type, true);
            TokenId = token_info.TokenId.ToInt64();
            IsDirectory = is_directory;
        }
    }

    /// <summary>
    /// Information about a token.
    /// </summary>
    public sealed class TokenInformation
    {
        /// <summary>
        /// Token username
        /// </summary>
        public Sid UserName { get; }
        /// <summary>
        /// Token integrity level
        /// </summary>
        public TokenIntegrityLevel IntegrityLevel { get; }
        /// <summary>
        /// Token type
        /// </summary>
        public TokenType TokenType { get; }
        /// <summary>
        /// Token impersonation level
        /// </summary>
        public SecurityImpersonationLevel ImpersonationLevel { get; }
        /// <summary>
        /// Token ID
        /// </summary>
        public Luid TokenId { get; }
        /// <summary>
        /// Elevated token
        /// </summary>
        public bool Elevated { get; }
        /// <summary>
        /// Restricted token
        /// </summary>
        public bool Restricted { get; }
        /// <summary>
        /// App container token
        /// </summary>
        public bool AppContainer { get; }
        /// <summary>
        /// App container SID (if an AppContainer)
        /// </summary>
        public Sid AppContainerSid { get; }
        /// <summary>
        /// Low privilege AC
        /// </summary>
        public bool LowPrivilegeAppContainer { get; }
        /// <summary>
        /// The session ID of the token.
        /// </summary>
        public int SessionId { get; }
        /// <summary>
        /// Additonal information of where the token was sourced from
        /// </summary>
        public Dictionary<string, object> SourceData { get; }

        /// <summary>
        /// Overridden ToString.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return $"User: {UserName}";
        }

        internal TokenInformation(NtToken token) : this(token, null)
        {
        }

        internal TokenInformation(NtToken token, NtProcess process)
        {
            SourceData = new Dictionary<string, object>();
            TokenId = token.Id;
            UserName = token.User.Sid;
            IntegrityLevel = token.IntegrityLevel;
            TokenType = token.TokenType;
            ImpersonationLevel = token.ImpersonationLevel;
            AppContainer = token.AppContainer;
            AppContainerSid = token.AppContainerSid;
            Elevated = token.Elevated;
            Restricted = token.Restricted;
            LowPrivilegeAppContainer = token.LowPrivilegeAppContainer;
            SessionId = token.SessionId;

            if (process != null)
            {
                SourceData["ProcessId"] = process.ProcessId;
                SourceData["Name"] = process.Name;
                SourceData["ImagePath"] = process.GetImageFilePath(false);
                SourceData["CommandLine"] = process.CommandLine;
            }
        }
    }

    internal struct TokenEntry : IDisposable
    {
        public readonly NtToken Token;
        public readonly TokenInformation Information;

        private static NtToken DuplicateToken(NtToken token)
        {
            if (token.TokenType == TokenType.Primary)
            {
                return token.DuplicateToken(TokenType.Impersonation, SecurityImpersonationLevel.Impersonation,
                    TokenAccessRights.Query | TokenAccessRights.Impersonate | TokenAccessRights.Duplicate);
            }
            else
            {
                return token.Duplicate(TokenAccessRights.Query | TokenAccessRights.Impersonate | TokenAccessRights.Duplicate);
            }
        }

        public TokenEntry(NtToken token) 
            : this(token, null)
        {
        }

        public TokenEntry(NtToken token, NtProcess process) 
            : this(token, token, process)
        {
        }

        public TokenEntry(NtToken token, NtToken imp_token, NtProcess process)
        {
            Information = new TokenInformation(token, process);
            Token = DuplicateToken(imp_token);
        }

        public void Dispose()
        {
            Token?.Dispose();
        }
    }

    /// <summary>
    /// Common base cmdlet for commands which look at accessible resources.
    /// </summary>
    public abstract class CommonAccessBaseCmdlet : Cmdlet
    {
        /// <summary>
        /// <para type="description">Specify a list of process IDs to open for their tokens.</para>
        /// </summary>
        [Parameter]
        public int[] ProcessIds { get; set; }

        /// <summary>
        /// <para type="description">Specify a list of process names to open for their tokens.</para>
        /// </summary>
        [Parameter]
        public string[] ProcessNames { get; set; }

        /// <summary>
        /// <para type="description">Specify a list of command lines to filter on find for the process tokens.</para>
        /// </summary>
        [Parameter]
        public string[] ProcessCommandLines { get; set; }

        /// <summary>
        /// <para type="description">Specify a list token objects.</para>
        /// </summary>
        [Parameter]
        public NtToken[] Tokens { get; set; }

        /// <summary>
        /// <para type="description">Specify a list of process objects to get tokens from.</para>
        /// </summary>
        [Parameter]
        public NtProcess[] Processes { get; set; }

        internal abstract void RunAccessCheck(IEnumerable<TokenEntry> tokens);

        internal void WriteAccessCheckResult(string name, string type_name, AccessMask granted_access,
            GenericMapping generic_mapping, string sddl, Type enum_type, bool is_directory, TokenInformation token_info)
        {
            WriteObject(new AccessCheckResult(name, type_name, granted_access, generic_mapping, sddl, enum_type, is_directory, token_info));
        }

        private static void AddTokenEntry(HashSet<TokenEntry> tokens, TokenEntry token)
        {
            if (!tokens.Add(token))
            {
                token.Dispose();
            }
        }

        private static NtToken GetTokenFromProcessDuplication(NtProcess process)
        {
            using (NtProcess dup_process = NtProcess.Open(process.ProcessId, ProcessAccessRights.QueryInformation))
            {
                using (var thread = dup_process.GetFirstThread(ThreadAccessRights.DirectImpersonation))
                {
                    if (thread == null)
                    {
                        throw new NtException(NtStatus.STATUS_ACCESS_DENIED);
                    }

                    using (NtThread.Current.ImpersonateThread(thread))
                    {
                        return NtThread.Current.OpenToken();
                    }
                }
            }
        }

        private static NtToken GetTokenFromProcessWithImpersonation(NtProcess process)
        {
            if (_system_token.Value != null)
            {
                using (_system_token.Value.Impersonate())
                {
                    using (var token = NtToken.OpenProcessToken(process,
                                            TokenAccessRights.Duplicate |
                                            TokenAccessRights.Impersonate |
                                            TokenAccessRights.Query, false))
                    {
                        if (!token.IsSuccess)
                        {
                            if (token.Status != NtStatus.STATUS_ACCESS_DENIED)
                            {
                                token.Status.ToNtException();
                            }
                        }
                        return token.Result.Duplicate();
                    }
                }
            }
            
            return GetTokenFromProcessDuplication(process);            
        }

        private static void AddTokenEntryFromProcess(HashSet<TokenEntry> tokens, NtProcess process)
        {
            using (var token = NtToken.OpenProcessToken(process, false, TokenAccessRights.Query))
            {
                using (var imp_token = token.DuplicateToken(TokenType.Impersonation, SecurityImpersonationLevel.Impersonation, 
                    TokenAccessRights.Query | TokenAccessRights.Impersonate | TokenAccessRights.Duplicate, false))
                {
                    NtToken valid_imp_token = null;
                    if (!imp_token.IsSuccess)
                    {
                        if (!_has_impersonate_privilege.Value || imp_token.Status != NtStatus.STATUS_ACCESS_DENIED)
                        {
                            imp_token.Status.ToNtException();
                        }

                        valid_imp_token = GetTokenFromProcessWithImpersonation(process);
                    }
                    else
                    {
                        valid_imp_token = imp_token.Result;
                    }
                    AddTokenEntry(tokens, new TokenEntry(token, valid_imp_token, process));
                }
            }
        }

        private void GetTokensFromPids(HashSet<TokenEntry> tokens, IEnumerable<int> pids)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using (NtProcess process = NtProcess.Open(pid, ProcessAccessRights.QueryLimitedInformation))
                    {
                        AddTokenEntryFromProcess(tokens, process);
                    }
                }
                catch (NtException ex)
                {
                    WriteError(new ErrorRecord(ex, "OpenTokenError", ErrorCategory.OpenError, $"pid:{pid}"));
                }
            }
        }

        private bool GetTokensFromArguments(HashSet<TokenEntry> tokens, IEnumerable<string> names, IEnumerable<string> cmdlines)
        {
            HashSet<string> names_set = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            HashSet<string> cmdline_set = new HashSet<string>(cmdlines ?? new string[0], StringComparer.OrdinalIgnoreCase);

            if (names_set.Count > 0 || cmdline_set.Count > 0)
            {
                using (var procs = NtProcess.GetProcesses(ProcessAccessRights.QueryLimitedInformation).ToDisposableList())
                {
                    foreach (NtProcess process in procs)
                    {
                        try
                        {
                            if (names_set.Contains(process.Name))
                            {
                                AddTokenEntryFromProcess(tokens, process);
                            }
                            else
                            {
                                string curr_cmdline = process.CommandLine.ToLower();
                                foreach (string cmdline in cmdline_set)
                                {
                                    if (curr_cmdline.Contains(cmdline.ToLower()))
                                    {
                                        AddTokenEntryFromProcess(tokens, process);
                                        break;
                                    }
                                }
                            }
                        }
                        catch (NtException ex)
                        {
                            WriteError(new ErrorRecord(ex, "OpenTokenError", ErrorCategory.OpenError, process.Name));
                        }
                    }
                }
                return true;
            }
            return false;
        }

        internal void WriteAccessWarning(string path, NtStatus status)
        {
            WriteWarning($"Couldn't access {path} - Status: {status}");
        }

        internal void WriteAccessWarning(NtObject root, string path, NtStatus status)
        {
            WriteAccessWarning($@"{root.FullPath.TrimEnd('\\')}\{path}", status);
        }

        private class TokenEntryComparer : IEqualityComparer<TokenEntry>
        {
            public bool Equals(TokenEntry x, TokenEntry y)
            {
                return x.Information.TokenId.Equals(y.Information.TokenId);
            }

            public int GetHashCode(TokenEntry obj)
            {
                return obj.Information.TokenId.GetHashCode();
            }
        }

        /// <summary>
        /// Overridden process record method.
        /// </summary>
        protected override void ProcessRecord()
        {
            HashSet<TokenEntry> tokens = new HashSet<TokenEntry>(new TokenEntryComparer());
            try
            {
                bool explicit_tokens = false;
                NtToken.EnableDebugPrivilege();

                if (Tokens != null)
                {
                    foreach (NtToken token in Tokens)
                    {
                        AddTokenEntry(tokens, new TokenEntry(token));
                    }
                    explicit_tokens = true;
                }

                if (ProcessIds != null)
                {
                    GetTokensFromPids(tokens, ProcessIds);
                    explicit_tokens = true;
                }

                if (GetTokensFromArguments(tokens, ProcessNames, ProcessCommandLines))
                {
                    explicit_tokens = true;
                }

                if (Processes != null)
                {
                    foreach (NtProcess process in Processes)
                    {
                        AddTokenEntryFromProcess(tokens, process);
                    }
                    explicit_tokens = true;
                }

                if (tokens.Count == 0)
                {
                    if (explicit_tokens)
                    {
                        return;
                    }

                    AddTokenEntryFromProcess(tokens, NtProcess.Current);
                }

                RunAccessCheck(tokens);
            }
            finally
            {
                foreach (TokenEntry token in tokens)
                {
                    token.Dispose();
                }
            }
        }

        private static bool CheckImpersonatePrivilege()
        {
            using (var token = NtProcess.Current.OpenToken())
            {
                var priv = token.GetPrivilege(TokenPrivilegeValue.SeImpersonatePrivilege);
                return priv != null ? priv.Enabled : false;
            }
        }

        private static NtToken FindSystemToken()
        {
            using (var ps = NtProcess.GetProcesses(ProcessAccessRights.QueryLimitedInformation).ToDisposableList())
            {
                foreach (var p in ps)
                {
                    if (p.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var token = NtToken.OpenProcessToken(p, TokenAccessRights.Duplicate, false))
                        {
                            if (!token.IsSuccess)
                            {
                                return null;
                            }
                            return token.Result.DuplicateToken(SecurityImpersonationLevel.Impersonation);
                        }
                    }
                }
            }
            return null;
        }

        private static Lazy<bool> _has_impersonate_privilege = new Lazy<bool>(CheckImpersonatePrivilege);
        private static Lazy<NtToken> _system_token = new Lazy<NtToken>(FindSystemToken);
    }

    /// <summary>
    /// Base class for accessible checks with an access parameter.
    /// </summary>
    /// <typeparam name="A">The type of access rights to check against.</typeparam>
    public abstract class CommonAccessBaseWithAccessCmdlet<A> : CommonAccessBaseCmdlet
    {
        /// <summary>
        /// <para type="description">Access rights to check for in an object's access.</para>
        /// </summary>
        [Parameter]
        public A AccessRights { get; set; }

        /// <summary>
        /// <para type="description">If AccessRights specified require that only part of the access rights
        /// are required to match an access check.</para>
        /// </summary>
        [Parameter]
        public SwitchParameter AllowPartialAccess { get; set; }

        /// <summary>
        /// <para type="description">If set an access entry will be generated even if granted access is 0.</para>
        /// </summary>
        [Parameter]
        public SwitchParameter AllowEmptyAccess { get; set; }

        internal bool IsAccessGranted(AccessMask granted_access, AccessMask access_rights)
        {
            if (granted_access.IsEmpty)
            {
                return AllowEmptyAccess;
            }

            if (access_rights.IsEmpty)
            {
                return true;
            }

            if (AllowPartialAccess)
            {
                return granted_access.IsAccessGranted(access_rights);
            }

            return granted_access.IsAllAccessGranted(access_rights);
        }
    }

    /// <summary>
    /// Base class for path based accessible checks.
    /// </summary>
    public abstract class GetAccessiblePathCmdlet<A> : CommonAccessBaseWithAccessCmdlet<A>
    {
        /// <summary>
        /// <para type="description">Specify a list of native paths to check.</para>
        /// </summary>
        [Parameter(Position = 0, ParameterSetName = "path", ValueFromPipeline = true)]
        public string[] Path { get; set; }

        /// <summary>
        /// <para type="description">Specify a list of paths in a Win32 format.</para>
        /// </summary>
        [Parameter(ParameterSetName = "path")]
        public string[] Win32Path { get; set; }

        /// <summary>
        /// <para type="description">When generating the results format path in Win32 format.</para>
        /// </summary>
        [Parameter]
        public SwitchParameter FormatWin32Path { get; set; }

        /// <summary>
        /// <para type="description">Specify whether to recursively check the path for access.</para>
        /// </summary>
        [Parameter(ParameterSetName = "path")]
        public SwitchParameter Recurse { get; set; }

        /// <summary>
        /// <para type="description">When recursing specify maximum depth.</para>
        /// </summary>
        [Parameter(ParameterSetName = "path")]
        public int? MaxDepth { get; set; }

        /// <summary>
        /// Convert a Win32 path to a native path.
        /// </summary>
        /// <param name="win32_path">The Win32 path to convert.</param>
        /// <returns>The converted native path.</returns>
        protected abstract string ConvertWin32Path(string win32_path);

        /// <summary>
        /// Run an access check with a path.
        /// </summary>
        /// <param name="tokens">The list of tokens.</param>
        /// <param name="path">The path to check.</param>
        internal abstract void RunAccessCheckPath(IEnumerable<TokenEntry> tokens, string path);

        internal override void RunAccessCheck(IEnumerable<TokenEntry> tokens)
        {
            List<string> paths = new List<string>();
            if (Path != null)
            {
                paths.AddRange(Path);
            }

            if (Win32Path != null)
            {
                paths.AddRange(Win32Path.Select(p => ConvertWin32Path(p)));
            }

            foreach (string path in paths)
            {
                if (!path.StartsWith(@"\"))
                {
                    WriteWarning($"Path '{path}' doesn't start with \\. Perhaps you want to specify -Win32Path instead?");
                }

                try
                {
                    RunAccessCheckPath(tokens, path);
                }
                catch (NtException ex)
                {
                    WriteError(new ErrorRecord(ex, "NtException", ErrorCategory.DeviceError, this));
                }
            }
        }

        internal int GetMaxDepth()
        {
            return MaxDepth ?? int.MaxValue;
        }
    }
}