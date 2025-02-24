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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NtApiDotNet
{
    /// <summary>
    /// Class representing a NT Process object.
    /// </summary>
    [NtType("Process")]
    public class NtProcess : NtObjectWithDuplicateAndInfo<NtProcess, ProcessAccessRights, ProcessInformationClass, ProcessInformationClass>
    {
        #region Private Members
        private int? _pid;
        private ProcessExtendedBasicInformation _extended_info;
        private bool? _wow64;

        private ProcessExtendedBasicInformation GetExtendedBasicInfo(bool get_cached)
        {
            if (_extended_info == null || !get_cached)
            {
                if (!IsAccessGranted(ProcessAccessRights.QueryLimitedInformation)
                    && !IsAccessGranted(ProcessAccessRights.QueryInformation))
                {
                    _extended_info = new ProcessExtendedBasicInformation();
                }
                else
                {
                    using (var buffer = Query(ProcessInformationClass.ProcessBasicInformation, new ProcessExtendedBasicInformation(), false))
                    {
                        if (buffer.IsSuccess)
                        {
                            _extended_info = buffer.Result;
                        }
                        else
                        {
                            ProcessExtendedBasicInformation result = new ProcessExtendedBasicInformation
                            {
                                BasicInfo = Query<ProcessBasicInformation>(ProcessInformationClass.ProcessBasicInformation)
                            };
                            _extended_info = result;
                        }
                    }
                }
            }

            return _extended_info;
        }

        private ProcessBasicInformation GetBasicInfo()
        {
            return GetExtendedBasicInfo(true).BasicInfo;
        }

        private static Enum ConvertPolicyToEnum(ProcessMitigationPolicy policy, int value)
        {
            switch (policy)
            {
                case ProcessMitigationPolicy.ImageLoad:
                    return (ProcessMitigationImageLoadPolicy)value;
                case ProcessMitigationPolicy.Signature:
                    return (ProcessMitigationBinarySignaturePolicy)value;
                case ProcessMitigationPolicy.ControlFlowGuard:
                    return (ProcessMitigationControlFlowGuardPolicy)value;
                case ProcessMitigationPolicy.DynamicCode:
                    return (ProcessMitigationDynamicCodePolicy)value;
                case ProcessMitigationPolicy.ExtensionPointDisable:
                    return (ProcessMitigationExtensionPointDisablePolicy)value;
                case ProcessMitigationPolicy.FontDisable:
                    return (ProcessMitigationFontDisablePolicy)value;
                case ProcessMitigationPolicy.StrictHandleCheck:
                    return (ProcessMitigationStrictHandleCheckPolicy)value;
                case ProcessMitigationPolicy.SystemCallDisable:
                    return (ProcessMitigationSystemCallDisablePolicy)value;
                case ProcessMitigationPolicy.ChildProcess:
                    return (ProcessMitigationChildProcessPolicy)value;
                case ProcessMitigationPolicy.PayloadRestriction:
                    return (ProcessMitigationPayloadRestrictionPolicy)value;
                case ProcessMitigationPolicy.SystemCallFilter:
                    return (ProcessMitigationSystemCallFilterPolicy)value;
                case ProcessMitigationPolicy.SideChannelIsolation:
                    return (ProcessMitigationSideChannelIsolationPolicy)value;
                case ProcessMitigationPolicy.ASLR:
                    return (ProcessMitigationAslrPolicy)value;
                default:
                    return (ProcessMitigationUnknownPolicy)value;
            }
        }

        private T QueryToken<T>(TokenAccessRights desired_access, Func<NtToken, T> callback, T default_value)
        {
            return NtToken.OpenProcessToken(this, desired_access, false).RunAndDispose(callback, default_value);
        }

        private T QueryToken<T>(Func<NtToken, T> callback, T default_value)
        {
            return QueryToken(TokenAccessRights.Query, callback, default_value);
        }

        private T QueryToken<T>(Func<NtToken, T> callback)
        {
            return QueryToken(TokenAccessRights.Query, callback, default(T));
        }

        #endregion

        #region Constructors

        internal NtProcess(SafeKernelObjectHandle handle) : base(handle)
        {
        }

        internal sealed class NtTypeFactoryImpl : NtTypeFactoryImplBase
        {
            public NtTypeFactoryImpl() : base(false)
            {
            }
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Gets all accessible processes on the system.
        /// </summary>
        /// <param name="desired_access">The access desired for each process.</param>
        /// <returns>The list of accessible processes.</returns>
        public static IEnumerable<NtProcess> GetProcesses(ProcessAccessRights desired_access)
        {
            return GetProcesses(desired_access, false);
        }

        /// <summary>
        /// Gets all accessible processes on the system.
        /// </summary>
        /// <param name="desired_access">The access desired for each process.</param>
        /// <param name="from_system_info">True to get processes from system information rather than NtGetNextProcess</param>
        /// <returns>The list of accessible processes.</returns>
        public static IEnumerable<NtProcess> GetProcesses(ProcessAccessRights desired_access, bool from_system_info)
        {
            using (var processes = new DisposableList<NtProcess>())
            {
                if (from_system_info)
                {
                    processes.AddRange(NtSystemInfo.GetProcessInformation().Select(p => Open(p.ProcessId, desired_access, false)).SelectValidResults());
                }
                else
                {
                    NtProcess process = NtProcess.GetFirstProcess(desired_access);
                    while (process != null)
                    {
                        processes.Add(process);
                        process = process.GetNextProcess(desired_access);
                    }
                }
                return processes.ToArrayAndClear();
            }
        }

        /// <summary>
        /// Gets all accessible processes on the system in a particular session.
        /// </summary>
        /// <param name="session_id">The session ID.</param>
        /// <param name="desired_access">The access desired for each process.</param>
        /// <returns>The list of accessible processes.</returns>
        public static IEnumerable<NtProcess> GetSessionProcesses(int session_id, ProcessAccessRights desired_access)
        {
            return NtSystemInfo.GetProcessInformation().Where(p => p.SessionId == session_id)
                .Select(p => Open(p.ProcessId, desired_access, false))
                .SelectValidResults().ToArray();
        }

        /// <summary>
        /// Gets all accessible processes on the system in the current session session.
        /// </summary>
        /// <param name="desired_access">The access desired for each process.</param>
        /// <returns>The list of accessible processes.</returns>
        public static IEnumerable<NtProcess> GetSessionProcesses(ProcessAccessRights desired_access)
        {
            return GetSessionProcesses(Current.SessionId, desired_access);
        }

        /// <summary>
        /// Get first accessible process (used in combination with GetNextProcess)
        /// </summary>
        /// <param name="desired_access">The access required for the process.</param>
        /// <returns>The accessible process, or null if one couldn't be opened.</returns>
        public static NtProcess GetFirstProcess(ProcessAccessRights desired_access)
        {
            NtStatus status = NtSystemCalls.NtGetNextProcess(SafeKernelObjectHandle.Null, desired_access,
                AttributeFlags.None, 0, out SafeKernelObjectHandle new_handle);
            if (status == NtStatus.STATUS_SUCCESS)
            {
                return new NtProcess(new_handle);
            }
            return null;
        }

        /// <summary>
        /// Open a process
        /// </summary>
        /// <param name="pid">The process ID to open</param>
        /// <param name="desired_access">The desired access for the handle</param>
        /// <param name="throw_on_error">True to throw an exception on error.</param>
        /// <returns>The NT status code and object result.</returns>
        public static NtResult<NtProcess> Open(int pid, ProcessAccessRights desired_access, bool throw_on_error)
        {
            ClientId client_id = new ClientId
            {
                UniqueProcess = new IntPtr(pid)
            };
            return NtSystemCalls.NtOpenProcess(out SafeKernelObjectHandle process, desired_access, new ObjectAttributes(), client_id)
                .CreateResult(throw_on_error, () => new NtProcess(process) { _pid = pid });
        }

        /// <summary>
        /// Open a process
        /// </summary>
        /// <param name="pid">The process ID to open</param>
        /// <param name="desired_access">The desired access for the handle</param>
        /// <returns>The opened process</returns>
        public static NtProcess Open(int pid, ProcessAccessRights desired_access)
        {
            return Open(pid, desired_access, true).Result;
        }

        /// <summary>
        /// Create a new process
        /// </summary>
        /// <param name="ParentProcess">The parent process</param>
        /// <param name="Flags">Creation flags</param>
        /// <param name="SectionHandle">Handle to the executable image section</param>
        /// <returns>The created process</returns>
        public static NtProcess CreateProcessEx(NtProcess ParentProcess, ProcessCreateFlags Flags, NtSection SectionHandle)
        {
            SafeHandle parent_process = ParentProcess != null ? ParentProcess.Handle : Current.Handle;
            SafeHandle section = SectionHandle?.Handle;
            NtSystemCalls.NtCreateProcessEx(out SafeKernelObjectHandle process, ProcessAccessRights.MaximumAllowed,
                new ObjectAttributes(), parent_process, Flags, section, null, null, 0).ToNtException();
            return new NtProcess(process);
        }

        /// <summary>
        /// Create a new process
        /// </summary>
        /// <param name="SectionHandle">Handle to the executable image section</param>
        /// <returns>The created process</returns>
        public static NtProcess CreateProcessEx(NtSection SectionHandle)
        {
            return CreateProcessEx(null, ProcessCreateFlags.None, SectionHandle);
        }

        /// <summary>
        /// Open an actual handle to the current process rather than the pseudo one used for Current
        /// </summary>
        /// <returns>The process object</returns>
        public static NtProcess OpenCurrent()
        {
            return Current.Duplicate();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Get next accessible process (used in combination with GetFirstProcess)
        /// </summary>
        /// <param name="desired_access">The access required for the process.</param>
        /// <returns>The accessible process, or null if one couldn't be opened.</returns>
        public NtProcess GetNextProcess(ProcessAccessRights desired_access)
        {
            NtStatus status = NtSystemCalls.NtGetNextProcess(Handle, desired_access, AttributeFlags.None, 0, out SafeKernelObjectHandle new_handle);
            if (status == NtStatus.STATUS_SUCCESS)
            {
                return new NtProcess(new_handle);
            }
            return null;
        }

        /// <summary>
        /// Get first accessible thread for process.
        /// </summary>
        /// <param name="desired_access">The desired access for the thread.</param>
        /// <returns>The first thread object, or null if not accessible threads.</returns>
        public NtThread GetFirstThread(ThreadAccessRights desired_access)
        {
            return NtThread.GetFirstThread(this, desired_access);
        }

        /// <summary>
        /// Get first accessible thread for process.
        /// </summary>
        /// <returns>The first thread object, or null if not accessible threads.</returns>
        public NtThread GetFirstThread()
        {
            return GetFirstThread(ThreadAccessRights.MaximumAllowed);
        }

        /// <summary>
        /// Get accessible threads for a process.
        /// </summary>
        /// <param name="desired_access">The desired access for the threads</param>
        /// <returns>The list of threads</returns>
        public IEnumerable<NtThread> GetThreads(ThreadAccessRights desired_access)
        {
            List<NtThread> handles = new List<NtThread>();
            if (IsAccessGranted(ProcessAccessRights.QueryInformation))
            {
                SafeKernelObjectHandle current_handle = new SafeKernelObjectHandle(IntPtr.Zero, false);
                NtStatus status = NtSystemCalls.NtGetNextThread(Handle, current_handle, desired_access, AttributeFlags.None, 0, out current_handle);
                while (status == NtStatus.STATUS_SUCCESS)
                {
                    handles.Add(new NtThread(current_handle));
                    status = NtSystemCalls.NtGetNextThread(Handle, current_handle, desired_access, AttributeFlags.None, 0, out current_handle);
                }
            }
            else
            {
                handles.AddRange(NtSystemInfo.GetThreadInformation(ProcessId).Select(t =>
                            NtThread.Open(t.ThreadId, desired_access, false)).SelectValidResults());
            }
            return handles;
        }

        /// <summary>
        /// Get accessible threads for a process.
        /// </summary>
        /// <returns>The list of threads</returns>
        public IEnumerable<NtThread> GetThreads()
        {
            return GetThreads(ThreadAccessRights.MaximumAllowed);
        }

        /// <summary>
        /// Read a partial PEB from the process.
        /// </summary>
        /// <returns>The read PEB structure.</returns>
        public IPeb GetPeb()
        {
            if (Wow64)
            {
                return NtVirtualMemory.ReadMemory<PartialPeb32>(Handle, PebAddress32.ToInt64());
            }
            return NtVirtualMemory.ReadMemory<PartialPeb>(Handle, PebAddress.ToInt64());
        }

        /// <summary>
        /// Create a new process
        /// </summary>
        /// <param name="Flags">Creation flags</param>
        /// <param name="SectionHandle">Handle to the executable image section</param>
        /// <returns>The created process</returns>
        public NtProcess CreateProcessEx(ProcessCreateFlags Flags, NtSection SectionHandle)
        {
            return CreateProcessEx(this, Flags, SectionHandle);
        }

        /// <summary>
        /// Terminate the process
        /// </summary>
        /// <param name="exitcode">The exit code for the termination</param>
        public void Terminate(NtStatus exitcode)
        {
            Terminate(exitcode, true);
        }

        /// <summary>
        /// Terminate the process
        /// </summary>
        /// <param name="exitcode">The exit code for the termination</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus Terminate(NtStatus exitcode, bool throw_on_error)
        {
            return NtSystemCalls.NtTerminateProcess(Handle, exitcode).ToNtException(throw_on_error);
        }

        /// <summary>
        /// Get process image file path
        /// </summary>
        /// <param name="native">True to return the native image path, false for a Win32 style path</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The process image file path</returns>
        public NtResult<string> GetImageFilePath(bool native, bool throw_on_error)
        {
            ProcessInformationClass info_class = native ? ProcessInformationClass.ProcessImageFileName : ProcessInformationClass.ProcessImageFileNameWin32;

            using (var result = QueryBuffer(info_class, new UnicodeStringOut(), throw_on_error))
            {
                return result.Map(s => s.Result.ToString());
            }
        }

        /// <summary>
        /// Get process image file path
        /// </summary>
        /// <param name="native">True to return the native image path, false for a Win32 style path</param>
        /// <returns>The process image file path</returns>
        public string GetImageFilePath(bool native)
        {
            return GetImageFilePath(native, true).Result;
        }

        /// <summary>
        /// Get a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to get</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The raw policy value</returns>
        public NtResult<int> GetRawMitigationPolicy(ProcessMitigationPolicy policy, bool throw_on_error)
        {
            switch (policy)
            {
                case ProcessMitigationPolicy.DEP:
                case ProcessMitigationPolicy.MitigationOptionsMask:
                    throw new ArgumentException("Invalid mitigation policy");
            }

            MitigationPolicy p = new MitigationPolicy
            {
                Policy = policy
            };

            return Query(ProcessInformationClass.ProcessMitigationPolicy, p, throw_on_error).Map(r => r.Result);
        }

        /// <summary>
        /// Get a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to get</param>
        /// <returns>The raw policy value</returns>
        public int GetRawMitigationPolicy(ProcessMitigationPolicy policy)
        {
            switch (policy)
            {
                case ProcessMitigationPolicy.DEP:
                case ProcessMitigationPolicy.MitigationOptionsMask:
                    throw new ArgumentException("Invalid mitigation policy");
            }

            MitigationPolicy p = new MitigationPolicy
            {
                Policy = policy
            };

            var result = GetRawMitigationPolicy(policy, false);
            switch (result.Status)
            {
                case NtStatus.STATUS_INVALID_PARAMETER:
                case NtStatus.STATUS_NOT_SUPPORTED:
                case NtStatus.STATUS_PROCESS_IS_TERMINATING:
                    return 0;
            }

            return result.GetResultOrThrow();
        }

        /// <summary>
        /// Get a mitigation policy as an enumeration.
        /// </summary>
        /// <param name="policy">The policy to get.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The mitigation policy value</returns>
        public NtResult<Enum> GetMitigationPolicy(ProcessMitigationPolicy policy, bool throw_on_error)
        {
            return GetRawMitigationPolicy(policy, throw_on_error).Map(i => ConvertPolicyToEnum(policy, i));
        }

        /// <summary>
        /// Get a mitigation policy as an enumeration.
        /// </summary>
        /// <param name="policy">The policy to get.</param>
        /// <returns>The mitigation policy value</returns>
        public Enum GetMitigationPolicy(ProcessMitigationPolicy policy)
        {
            return GetMitigationPolicy(policy, true).Result;
        }

        /// <summary>
        /// Get a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to get</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The raw policy value</returns>
        [Obsolete("Use GetRawMitigationPolicy or GetMitigationPolicy")]
        public NtResult<int> GetProcessMitigationPolicy(ProcessMitigationPolicy policy, bool throw_on_error)
        {
            return GetRawMitigationPolicy(policy, throw_on_error);
        }

        /// <summary>
        /// Get a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to get</param>
        /// <returns>The raw policy value</returns>
        [Obsolete("Use GetRawMitigationPolicy or GetMitigationPolicy")]
        public int GetProcessMitigationPolicy(ProcessMitigationPolicy policy)
        {
            return GetRawMitigationPolicy(policy);
        }

        /// <summary>
        /// Set a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus SetRawMitigationPolicy(ProcessMitigationPolicy policy, int value, bool throw_on_error)
        {
            switch (policy)
            {
                case ProcessMitigationPolicy.DEP:
                case ProcessMitigationPolicy.MitigationOptionsMask:
                    throw new ArgumentException("Invalid mitigation policy");
            }

            MitigationPolicy p = new MitigationPolicy()
            {
                Policy = policy,
                Result = value
            };

            return Set(ProcessInformationClass.ProcessMitigationPolicy, p, throw_on_error);
        }

        /// <summary>
        /// Set a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        public void SetRawMitigationPolicy(ProcessMitigationPolicy policy, int value)
        {
            SetRawMitigationPolicy(policy, value, true);
        }

        /// <summary>
        /// Set a mitigation policy value from an enum.
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus SetMitigationPolicy(ProcessMitigationPolicy policy, Enum value, bool throw_on_error)
        {
            return SetRawMitigationPolicy(policy, Convert.ToInt32(value), throw_on_error);
        }

        /// <summary>
        /// Set a mitigation policy value from an enum.
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        public void SetMitigationPolicy(ProcessMitigationPolicy policy, Enum value)
        {
            SetMitigationPolicy(policy, value, true);
        }

        /// <summary>
        /// Set a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        [Obsolete("Use SetMitigationPolicy or SetRawMitigationPolicy")]
        public NtStatus SetProcessMitigationPolicy(ProcessMitigationPolicy policy, int value, bool throw_on_error)
        {
            return SetRawMitigationPolicy(policy, value, throw_on_error);
        }

        /// <summary>
        /// Set a mitigation policy raw value
        /// </summary>
        /// <param name="policy">The policy to set</param>
        /// <param name="value">The value to set</param>
        [Obsolete("Use SetMitigationPolicy or SetRawMitigationPolicy")]
        public void SetProcessMitigationPolicy(ProcessMitigationPolicy policy, int value)
        {
            SetRawMitigationPolicy(policy, value);
        }

        /// <summary>
        /// Disable dynamic code policy on another process.
        /// </summary>
        public void DisableDynamicCodePolicy()
        {
            if (!NtToken.EnableDebugPrivilege())
            {
                throw new InvalidOperationException("Must have Debug privilege to disable code policy");
            }

            SetRawMitigationPolicy(ProcessMitigationPolicy.DynamicCode, 0);
        }

        /// <summary>
        /// Suspend the entire process.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus Suspend(bool throw_on_error)
        {
            return NtSystemCalls.NtSuspendProcess(Handle).ToNtException(throw_on_error);
        }

        /// <summary>
        /// Resume the entire process.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus Resume(bool throw_on_error)
        {
            return NtSystemCalls.NtResumeProcess(Handle).ToNtException(throw_on_error);
        }

        /// <summary>
        /// Suspend the entire process.
        /// </summary>
        public void Suspend()
        {
            Suspend(true);
        }

        /// <summary>
        /// Resume the entire process.
        /// </summary>
        public void Resume()
        {
            Resume(true);
        }

        /// <summary>
        /// Open the process' token
        /// </summary>
        /// <returns></returns>
        public NtToken OpenToken()
        {
            return NtToken.OpenProcessToken(this, false);
        }

        /// <summary>
        /// Set process access token. Process must be have not been started.
        /// </summary>
        /// <param name="token">The token to set.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus SetToken(NtToken token, bool throw_on_error)
        {
            ProcessAccessToken proc_token = new ProcessAccessToken
            {
                AccessToken = token.Handle.DangerousGetHandle()
            };
            return Set(ProcessInformationClass.ProcessAccessToken, proc_token, throw_on_error);
        }

        /// <summary>
        /// Set process access token. Process must be have not been started.
        /// </summary>
        /// <param name="token">The token to set.</param>
        public void SetToken(NtToken token)
        {
            SetToken(token, true);
        }

        /// <summary>
        /// Read memory from a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="length">The length to read.</param>
        /// <param name="read_all">If true ensure we read all bytes, otherwise throw on exception.</param>
        /// <returns>The array of bytes read from the location. 
        /// If a read is short then returns fewer bytes than requested.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public byte[] ReadMemory(long base_address, int length, bool read_all)
        {
            byte[] ret = NtVirtualMemory.ReadMemory(Handle, base_address, length);
            if (read_all && length != ret.Length)
            {
                throw new NtException(NtStatus.STATUS_PARTIAL_COPY);
            }
            return ret;
        }

        /// <summary>
        /// Read memory from a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="length">The length to read.</param>
        /// <returns>The array of bytes read from the location. 
        /// If a read is short then returns fewer bytes than requested.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public byte[] ReadMemory(long base_address, int length)
        {
            return ReadMemory(base_address, length, false);
        }

        /// <summary>
        /// Write memory to a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="data">The data to write.</param>
        /// <returns>The number of bytes written to the location</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public int WriteMemory(long base_address, byte[] data)
        {
            return NtVirtualMemory.WriteMemory(Handle, base_address, data);
        }

        /// <summary>
        /// Read structured memory from a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <returns>The read structure.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        /// <typeparam name="T">Type of structure to read.</typeparam>
        public T ReadMemory<T>(long base_address) where T : new()
        {
            return NtVirtualMemory.ReadMemory<T>(Handle, base_address);
        }

        /// <summary>
        /// Write structured memory to a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="data">The data to write.</param>
        /// <exception cref="NtException">Thrown on error.</exception>
        /// <typeparam name="T">Type of structure to write.</typeparam>
        public void WriteMemory<T>(long base_address, T data) where T : new()
        {
            NtVirtualMemory.WriteMemory(Handle, base_address, data);
        }

        /// <summary>
        /// Read structured memory array from a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="count">The number of elements in the array to read.</param>
        /// <returns>The read structure.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        /// <typeparam name="T">Type of structure to read.</typeparam>
        public T[] ReadMemoryArray<T>(long base_address, int count) where T : new()
        {
            return NtVirtualMemory.ReadMemoryArray<T>(Handle, base_address, count);
        }

        /// <summary>
        /// Write structured memory array to a process.
        /// </summary>
        /// <param name="base_address">The base address in the process.</param>
        /// <param name="data">The data array to write.</param>
        /// <exception cref="NtException">Thrown on error.</exception>
        /// <typeparam name="T">Type of structure to write.</typeparam>
        public void WriteMemoryArray<T>(long base_address, T[] data) where T : new()
        {
            NtVirtualMemory.WriteMemoryArray(Handle, base_address, data);
        }

        /// <summary>
        /// Query memory information for a process.
        /// </summary>
        /// <param name="base_address">The base address.</param>
        /// <returns>The queries memory information.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public MemoryInformation QueryMemoryInformation(long base_address)
        {
            return NtVirtualMemory.QueryMemoryInformation(Handle, base_address);
        }

        /// <summary>
        /// Query all memory information regions in process memory.
        /// </summary>
        /// <returns>The list of memory regions.</returns>
        /// <param name="include_free_regions">True to include free regions of memory.</param>
        /// <exception cref="NtException">Thrown on error.</exception>
        public IEnumerable<MemoryInformation> QueryAllMemoryInformation(bool include_free_regions)
        {
            IEnumerable<MemoryInformation> mem_infos = NtVirtualMemory.QueryMemoryInformation(Handle);
            if (!include_free_regions)
            {
                return mem_infos.Where(m => m.State != MemoryState.Free);
            }
            return mem_infos;
        }

        /// <summary>
        /// Query all memory information regions in process memory excluding free regions.
        /// </summary>
        /// <returns>The list of memory regions.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public IEnumerable<MemoryInformation> QueryAllMemoryInformation()
        {
            return QueryAllMemoryInformation(false);
        }

        /// <summary>
        /// Query a list of mapped images in a process.
        /// </summary>
        /// <returns>The list of mapped images</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public IEnumerable<MappedFile> QueryMappedImages()
        {
            return QueryAllMappedFiles().Where(m => m.IsImage);
        }

        /// <summary>
        /// Query a list of mapped files in a process.
        /// </summary>
        /// <returns>The list of mapped images</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public IEnumerable<MappedFile> QueryMappedFiles()
        {
            return QueryAllMappedFiles().Where(m => !m.IsImage);
        }

        /// <summary>
        /// Query a list of all mapped files and images in a process.
        /// </summary>
        /// <returns>The list of mapped images</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public IEnumerable<MappedFile> QueryAllMappedFiles()
        {
            return NtVirtualMemory.QueryMappedFiles(Handle);
        }

        /// <summary>
        /// Allocate virtual memory in a process.
        /// </summary>
        /// <param name="base_address">Optional base address, if 0 will automatically select a base.</param>
        /// <param name="region_size">The region size to allocate.</param>
        /// <param name="allocation_type">The type of allocation.</param>
        /// <param name="protect">The allocation protection.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The address of the allocated region.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtResult<long> AllocateMemory(long base_address,
            long region_size,
            MemoryAllocationType allocation_type, MemoryAllocationProtect protect,
            bool throw_on_error)
        {
            return NtVirtualMemory.AllocateMemory(Handle, base_address,
                region_size, allocation_type, protect, throw_on_error);
        }

        /// <summary>
        /// Allocate virtual memory in a process.
        /// </summary>
        /// <param name="base_address">Optional base address, if 0 will automatically select a base.</param>
        /// <param name="region_size">The region size to allocate.</param>
        /// <param name="allocation_type">The type of allocation.</param>
        /// <param name="protect">The allocation protection.</param>
        /// <returns>The address of the allocated region.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public long AllocateMemory(long base_address,
            long region_size,
            MemoryAllocationType allocation_type, MemoryAllocationProtect protect)
        {
            return AllocateMemory(base_address, region_size, allocation_type, protect, true).Result;
        }

        /// <summary>
        /// Allocate read/write virtual memory in a process.
        /// </summary>
        /// <param name="region_size">The region size to allocate.</param>
        /// <returns>The address of the allocated region.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public long AllocateMemory(long region_size)
        {
            return AllocateMemory(0, region_size,
                MemoryAllocationType.Reserve | MemoryAllocationType.Commit,
                MemoryAllocationProtect.ReadWrite);
        }

        /// <summary>
        /// Free virtual emmory in a process.
        /// </summary>
        /// <param name="base_address">Base address of region to free</param>
        /// <param name="region_size">The size of the region.</param>
        /// <param name="free_type">The type to free.</param>
        /// <exception cref="NtException">Thrown on error.</exception>
        public void FreeMemory(long base_address, long region_size, MemoryFreeType free_type)
        {
            NtVirtualMemory.FreeMemory(Handle, base_address, region_size, free_type);
        }

        /// <summary>
        /// Free virtual emmory in a process.
        /// </summary>
        /// <param name="base_address">Base address of region to free</param>
        /// <param name="region_size">The size of the region.</param>
        /// <param name="free_type">The type to free.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtStatus FreeMemory(long base_address, long region_size, MemoryFreeType free_type, bool throw_on_error)
        {
            return NtVirtualMemory.FreeMemory(Handle, base_address, region_size, free_type, throw_on_error);
        }

        /// <summary>
        /// Change protection on a region of memory.
        /// </summary>
        /// <param name="base_address">The base address</param>
        /// <param name="region_size">The size of the memory region.</param>
        /// <param name="new_protect">The new protection type.</param>
        /// <returns>The old protection for the region.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public MemoryAllocationProtect ProtectMemory(long base_address,
            long region_size, MemoryAllocationProtect new_protect)
        {
            return NtVirtualMemory.ProtectMemory(Handle, base_address,
                region_size, new_protect);
        }

        /// <summary>
        /// Change protection on a region of memory.
        /// </summary>
        /// <param name="base_address">The base address</param>
        /// <param name="region_size">The size of the memory region.</param>
        /// <param name="new_protect">The new protection type.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The old protection for the region.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtResult<MemoryAllocationProtect> ProtectMemory(long base_address,
            long region_size, MemoryAllocationProtect new_protect, bool throw_on_error)
        {
            return NtVirtualMemory.ProtectMemory(Handle, base_address,
                region_size, new_protect, throw_on_error);
        }

        /// <summary>
        /// Query working set information for an address in a process.
        /// </summary>
        /// <param name="base_address">The base address to query.</param>
        /// <param name="throw_on_error">True to throw on error</param>
        /// <returns>The working set information.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtResult<MemoryWorkingSetExInformation> QueryWorkingSetEx(long base_address, bool throw_on_error)
        {
            return NtVirtualMemory.QueryWorkingSetEx(Handle, base_address, throw_on_error);
        }

        /// <summary>
        /// Query working set information for an address in a process.
        /// </summary>
        /// <param name="base_address">The base address to query.</param>
        /// <returns>The working set information.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public MemoryWorkingSetExInformation QueryWorkingSetEx(long base_address)
        {
            return QueryWorkingSetEx(base_address, true).Result;
        }

        /// <summary>
        /// Set the process device map.
        /// </summary>
        /// <param name="device_map">The device map directory to set.</param>
        /// <remarks>Note that due to a bug in the Wow64 layer this won't work in a 32 bit process on a 64 bit system.</remarks>
        public void SetDeviceMap(NtDirectory device_map)
        {
            SetDeviceMap(device_map, true);
        }

        /// <summary>
        /// Set the process device map.
        /// </summary>
        /// <param name="device_map">The device map directory to set.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <remarks>Note that due to a bug in the Wow64 layer this won't work in a 32 bit process on a 64 bit system.</remarks>
        public NtStatus SetDeviceMap(NtDirectory device_map, bool throw_on_error)
        {
            var device_map_set = new ProcessDeviceMapInformationSet
            {
                DirectoryHandle = device_map.Handle.DangerousGetHandle()
            };

            return Set(ProcessInformationClass.ProcessDeviceMap, device_map_set, throw_on_error);
        }

        /// <summary>
        /// Set the process device map.
        /// </summary>
        /// <param name="device_map">The device map directory to set.</param>
        /// <remarks>Note that due to a bug in the Wow64 layer this won't work in a 32 bit process on a 64 bit system.</remarks>
        [Obsolete("Use SetDeviceMap")]
        public void SetProcessDeviceMap(NtDirectory device_map)
        {
            SetDeviceMap(device_map);
        }

        /// <summary>
        /// Set the process device map.
        /// </summary>
        /// <param name="device_map">The device map directory to set.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <remarks>Note that due to a bug in the Wow64 layer this won't work in a 32 bit process on a 64 bit system.</remarks>
        [Obsolete("Use SetDeviceMap")]
        public NtStatus SetProcessDeviceMap(NtDirectory device_map, bool throw_on_error)
        {
            return SetDeviceMap(device_map, throw_on_error);
        }

        /// <summary>
        /// Open a process' debug object.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The process' debug object.</returns>
        public NtResult<NtDebug> OpenDebugObject(bool throw_on_error)
        {
            return Query(ProcessInformationClass.ProcessDebugObjectHandle, IntPtr.Zero, throw_on_error).Map(r => NtDebug.FromHandle(r, true));
        }

        /// <summary>
        /// Open a process' debug object.
        /// </summary>
        /// <returns>The process' debug object.</returns>
        public NtDebug OpenDebugObject()
        {

            return OpenDebugObject(true).Result;
        }

        /// <summary>
        /// Queries whether process is backed by a specific file.
        /// </summary>
        /// <param name="file">File object opened with Synchronize and Execute access to test against.</param>
        /// <returns>True if the process is created from the image file.</returns>
        public bool IsImageFile(NtFile file)
        {
            return Query(ProcessInformationClass.ProcessImageFileMapping, file.Handle.DangerousGetHandle(), false).IsSuccess;
        }

        /// <summary>
        /// Open parent process by ID.
        /// </summary>
        /// <param name="desired_access">The desired process access rights.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtResult<NtProcess> OpenParent(ProcessAccessRights desired_access, bool throw_on_error)
        {
            return Open(ParentProcessId, desired_access, throw_on_error);
        }

        /// <summary>
        /// Open parent process by ID.
        /// </summary>
        /// <param name="desired_access">The desired process access rights.</param>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtProcess OpenParent(ProcessAccessRights desired_access)
        {
            return OpenParent(desired_access, true).Result;
        }

        /// <summary>
        /// Open parent process by ID.
        /// </summary>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtProcess OpenParent()
        {
            return OpenParent(ProcessAccessRights.MaximumAllowed);
        }

        /// <summary>
        /// Open owner process by ID.
        /// </summary>
        /// <param name="desired_access">The desired process access rights.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtResult<NtProcess> OpenOwner(ProcessAccessRights desired_access, bool throw_on_error)
        {
            return Open(OwnerProcessId, desired_access, throw_on_error);
        }

        /// <summary>
        /// Open owner process by ID.
        /// </summary>
        /// <param name="desired_access">The desired process access rights.</param>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtProcess OpenOwner(ProcessAccessRights desired_access)
        {
            return OpenOwner(desired_access, true).Result; ;
        }

        /// <summary>
        /// Open owner process by ID.
        /// </summary>
        /// <returns>The opened process.</returns>
        /// <exception cref="NtException">Thrown on error.</exception>
        public NtProcess OpenOwner()
        {
            return OpenOwner(ProcessAccessRights.MaximumAllowed);
        }

        /// <summary>
        /// Get if process is in a job.
        /// </summary>
        /// <param name="job">A specific job to check</param>
        /// <returns>True if in specific job.</returns>
        public bool IsInJob(NtJob job)
        {
            return NtSystemCalls.NtIsProcessInJob(Handle,
                job.GetHandle()) == NtStatus.STATUS_PROCESS_IN_JOB;
        }

        /// <summary>
        /// Get if process is in a job.
        /// </summary>
        /// <returns>True if in a job.</returns>
        public bool IsInJob()
        {
            return IsInJob(null);
        }

        /// <summary>
        /// Get process handle table.
        /// </summary>
        /// <returns>The list of process handles.</returns>
        public IEnumerable<int> GetHandleTable()
        {
            // Try handle count + 1000 (just to give a bit of space)
            // If you want this to be reliable you probably need to suspend the process.
            using (var buf = new SafeHGlobalBuffer((HandleCount + 1000) * 4))
            {
                NtSystemCalls.NtQueryInformationProcess(Handle, ProcessInformationClass.ProcessHandleTable,
                    buf, buf.Length, out int return_length).ToNtException();
                int[] ret = new int[return_length / 4];
                buf.ReadArray(0, ret, 0, ret.Length);
                return ret;
            }
        }

        /// <summary>
        /// Get the process handle table and try and get them as objects.
        /// </summary>
        /// <param name="named_only">True to only return named objects</param>
        /// <param name="type_names">A list of typenames to filter on (if empty then return all)</param>
        /// <returns>The list of handles as objects.</returns>
        /// <remarks>This function will drop handles it can't duplicate.</remarks>
        public IEnumerable<NtObject> GetHandleTableAsObjects(bool named_only, IEnumerable<string> type_names)
        {
            if (!IsAccessGranted(ProcessAccessRights.DupHandle))
            {
                return new NtObject[0];
            }

            List<NtObject> objs = new List<NtObject>();
            HashSet<string> types = new HashSet<string>(type_names, StringComparer.OrdinalIgnoreCase);
            foreach (int handle in GetHandleTable())
            {
                try
                {
                    using (NtGeneric generic = NtGeneric.DuplicateFrom(this, new IntPtr(handle)))
                    {
                        if (named_only && generic.FullPath == String.Empty)
                        {
                            continue;
                        }

                        if (types.Count > 0 && !types.Contains(generic.NtTypeName))
                        {
                            continue;
                        }

                        objs.Add(generic.ToTypedObject());
                    }
                }
                catch (NtException)
                {
                }
            }
            return objs;
        }

        /// <summary>
        /// Get the process handle table and try and get them as objects.
        /// </summary>
        /// <returns>The list of handles as objects.</returns>
        /// <remarks>This function will drop handles it can't duplicate.</remarks>
        public IEnumerable<NtObject> GetHandleTableAsObjects()
        {
            return GetHandleTableAsObjects(false, new string[0]);
        }

        /// <summary>
        /// Open image section for process.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The opened image section.</returns>
        /// <remarks>Should only work on the pseudo process handle.</remarks>
        public NtResult<NtSection> OpenImageSection(bool throw_on_error)
        {
            return Query(ProcessInformationClass.ProcessImageSection, 
                IntPtr.Zero, throw_on_error).Map(r => NtSection.FromHandle(r, true));
        }

        /// <summary>
        /// Open image section for process.
        /// </summary>
        /// <returns>The opened image section.</returns>
        /// <remarks>Should only work on the pseudo process handle.</remarks>
        public NtSection OpenImageSection()
        {
            return OpenImageSection(true).Result;
        }

        /// <summary>
        /// Unmap a section.
        /// </summary>
        /// <param name="base_address">The base address to unmap.</param>
        /// <param name="flags">Flags for unmapping memory.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus Unmap(IntPtr base_address, MemUnmapFlags flags, bool throw_on_error)
        {
            return NtSection.Unmap(this, base_address, flags, throw_on_error);
        }

        /// <summary>
        /// Unmap a section.
        /// </summary>
        /// <param name="base_address">The base address to unmap.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The NT status code.</returns>
        public NtStatus Unmap(IntPtr base_address, bool throw_on_error)
        {
            return Unmap(base_address, MemUnmapFlags.None, throw_on_error);
        }

        /// <summary>
        /// Unmap a section.
        /// </summary>
        /// <param name="base_address">The base address to unmap.</param>
        /// <param name="flags">Flags for unmapping memory.</param>
        public void Unmap(IntPtr base_address, MemUnmapFlags flags)
        {
            Unmap(base_address, flags, true);
        }

        /// <summary>
        /// Unmap a section.
        /// </summary>
        /// <param name="base_address">The base address to unmap.</param>
        public void Unmap(IntPtr base_address)
        {
            Unmap(base_address, true);
        }

        /// <summary>
        /// Method to query information for this object type.
        /// </summary>
        /// <param name="info_class">The information class.</param>
        /// <param name="buffer">The buffer to return data in.</param>
        /// <param name="return_length">Return length from the query.</param>
        /// <returns>The NT status code for the query.</returns>
        public override NtStatus QueryInformation(ProcessInformationClass info_class, SafeBuffer buffer, out int return_length)
        {
            return NtSystemCalls.NtQueryInformationProcess(Handle, info_class, buffer, buffer.GetLength(), out return_length);
        }

        /// <summary>
        /// Method to set information for this object type.
        /// </summary>
        /// <param name="info_class">The information class.</param>
        /// <param name="buffer">The buffer to set data from.</param>
        /// <returns>The NT status code for the set.</returns>
        public override NtStatus SetInformation(ProcessInformationClass info_class, SafeBuffer buffer)
        {
            return NtSystemCalls.NtSetInformationProcess(Handle, info_class, buffer, buffer.GetLength());
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Get the process' session ID
        /// </summary>
        public int SessionId
        {
            get
            {
                return Query<ProcessSessionInformation>(ProcessInformationClass.ProcessSessionInformation).SessionId;
            }
        }

        /// <summary>
        /// Get the process' ID
        /// </summary>
        public int ProcessId
        {
            get
            {
                if (!_pid.HasValue)
                {
                    _pid = GetBasicInfo().UniqueProcessId.ToInt32();
                }
                return _pid.Value;
            }
        }

        /// <summary>
        /// Get the process' parent process ID
        /// </summary>
        public int ParentProcessId
        {
            get
            {
                return GetBasicInfo().InheritedFromUniqueProcessId.ToInt32();
            }
        }

        /// <summary>
        /// Get the memory address of the PEB
        /// </summary>
        public IntPtr PebAddress
        {
            get
            {
                return GetBasicInfo().PebBaseAddress;
            }
        }

        /// <summary>
        /// Get the memory address of the PEB for a 32 bit process.
        /// </summary>
        /// <remarks>If the process is 64 bit, or the OS is 32 bit this returns the same value as PebAddress.</remarks>
        public IntPtr PebAddress32
        {
            get
            {
                if (!Wow64)
                {
                    return PebAddress;
                }
                return Query<IntPtr>(ProcessInformationClass.ProcessWow64Information);
            }
        }

        /// <summary>
        /// Get the base address of the process from the PEB.
        /// </summary>
        public IntPtr ImageBaseAddress
        {
            get
            {
                return GetPeb().GetImageBaseAddress();
            }
        }

        /// <summary>
        /// Read flags from PEB.
        /// </summary>
        public PebFlags PebFlags
        {
            get
            {
                return GetPeb().GetPebFlags();
            }
        }

        /// <summary>
        /// Get the process' exit status.
        /// </summary>
        public int ExitStatus
        {
            get
            {
                return GetExtendedBasicInfo(false).BasicInfo.ExitStatus;
            }
        }

        /// <summary>
        /// Get the process' command line
        /// </summary>
        public string CommandLine
        {
            get
            {
                using (var result = QueryBuffer(ProcessInformationClass.ProcessCommandLineInformation, new UnicodeStringOut(), false))
                {
                    // This will fail if process is being torn down, just return an empty string.
                    if (result.Status == NtStatus.STATUS_PROCESS_IS_TERMINATING
                        || result.Status == NtStatus.STATUS_PARTIAL_COPY
                        || result.Status == NtStatus.STATUS_NOT_FOUND)
                    {
                        return string.Empty;
                    }

                    return result.GetResultOrThrow().Result.ToString();
                }
            }
        }

        /// <summary>
        /// Get the command line as parsed arguments.
        /// </summary>
        public string[] CommandLineArguments => Win32Utils.ParseCommandLine(CommandLine);

        /// <summary>
        /// Get process DEP status
        /// </summary>
        public ProcessDepStatus DepStatus
        {
            get
            {
                using (SafeStructureInOutBuffer<uint> buffer = new SafeStructureInOutBuffer<uint>())
                {
                    ProcessDepStatus ret = new ProcessDepStatus();
                    NtStatus status = NtSystemCalls.NtQueryInformationProcess(Handle, ProcessInformationClass.ProcessExecuteFlags, buffer, buffer.Length, out int return_length);
                    if (!status.IsSuccess())
                    {
                        if (status != NtStatus.STATUS_INVALID_PARAMETER)
                        {
                            status.ToNtException();
                        }
                        else if (Is64Bit)
                        {
                            // On 64 bits OS, DEP is always ON for 64 bits processes
                            ret.Enabled = true;
                            ret.Permanent = true;
                        }

                        return ret;
                    }

                    uint result = buffer.Result;
                    if ((result & 2) == 0)
                    {
                        ret.Enabled = true;
                        if ((result & 4) != 0)
                        {
                            ret.DisableAtlThunkEmulation = true;
                        }
                    }
                    if ((result & 8) != 0)
                    {
                        ret.Permanent = true;
                    }
                    return ret;
                }
            }
        }

        /// <summary>
        /// Get whether process has a debug port.
        /// </summary>
        /// <returns></returns>
        public bool HasDebugPort
        {
            get
            {
                return Query<IntPtr>(ProcessInformationClass.ProcessDebugPort) != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Get handle count.
        /// </summary>
        public int HandleCount
        {
            get
            {
                // Weirdly if you query for 8 bytes it just returns count in upper and lower bits.
                return Query<int>(ProcessInformationClass.ProcessHandleCount);
            }
        }

        /// <summary>
        /// Get break on termination flag.
        /// </summary>
        public bool BreakOnTermination
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessBreakOnTermination) != 0;
            }
        }

        /// <summary>
        /// Get debug flags.
        /// </summary>
        public int DebugFlags
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessDebugFlags);
            }
        }

        /// <summary>
        /// Get execute flags.
        /// </summary>
        public int ExecuteFlags
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessExecuteFlags);
            }
        }

        /// <summary>
        /// Get IO priority.
        /// </summary>
        public int IoPriority
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessIoPriority);
            }
        }

        /// <summary>
        /// Get secure cookie.
        /// </summary>
        public int Cookie
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessCookie);
            }
        }

        /// <summary>
        /// Get the process user.
        /// </summary>
        public Sid User
        {
            get
            {
                using (NtToken token = OpenToken())
                {
                    return token.User.Sid;
                }
            }
        }

        /// <summary>
        /// Get process mitigations
        /// </summary>
        public NtProcessMitigations Mitigations
        {
            get
            {
                return new NtProcessMitigations(this);
            }
        }

        /// <summary>
        /// Get extended process flags.
        /// </summary>
        public ProcessExtendedBasicInformationFlags ExtendedFlags
        {
            get
            {
                return GetExtendedBasicInfo(false).Flags;
            }
        }

        /// <summary>
        /// Get process window title (from Process Parameters).
        /// </summary>
        public string WindowTitle
        {
            get
            {
                using (var buf = QueryBuffer<ProcessWindowInformation>(ProcessInformationClass.ProcessWindowInformation))
                {
                    ProcessWindowInformation window_info = buf.Result;
                    return buf.Data.ReadUnicodeString(window_info.WindowTitleLength / 2);
                }
            }
        }

        /// <summary>
        /// Get process window flags (from Process Parameters).
        /// </summary>
        public uint WindowFlags
        {
            get
            {
                using (var buf = QueryBuffer<ProcessWindowInformation>(ProcessInformationClass.ProcessWindowInformation))
                {
                    return buf.Result.WindowFlags;
                }
            }
        }

        /// <summary>
        /// Get the process subsystem type.
        /// </summary>
        public ProcessSubsystemInformationType SubsystemType
        {
            get
            {
                return (ProcessSubsystemInformationType)Query<int>(ProcessInformationClass.ProcessSubsystemInformation);
            }
        }

        /// <summary>
        /// Get if the process is Wow64
        /// </summary>
        public bool Wow64
        {
            get
            {
                if (!_wow64.HasValue)
                {
                    _wow64 = Query<IntPtr>(ProcessInformationClass.ProcessWow64Information) != IntPtr.Zero;
                }
                return _wow64.Value;
            }
        }

        /// <summary>
        /// Get whether the process is 64bit.
        /// </summary>
        public bool Is64Bit
        {
            get
            {
                return Environment.Is64BitOperatingSystem && !Wow64;
            }
        }


        /// <summary>
        /// Get whether LUID device maps are enabled.
        /// </summary>
        public bool LUIDDeviceMapsEnabled
        {
            get
            {
                return Query<int>(ProcessInformationClass.ProcessLUIDDeviceMapsEnabled) != 0;
            }
        }

        /// <summary>
        /// Return whether this process is sandboxed.
        /// </summary>
        public bool IsSandboxToken
        {
            get
            {
                return QueryToken(token => token.IsSandbox);
            }
        }

        /// <summary>
        /// Get or set the hard error mode.
        /// </summary>
        public int HardErrorMode
        {
            get
            {
                var result = Query(ProcessInformationClass.ProcessDefaultHardErrorMode, 0, false);
                if (result.IsSuccess)
                {
                    return result.Result;
                }
                return 0;
            }

            set
            {
                Set(ProcessInformationClass.ProcessDefaultHardErrorMode, value);
            }
        }


        /// <summary>
        /// Get the process handle table and try and get them as objects.
        /// </summary>
        /// <returns>The list of handles as objects.</returns>
        /// <remarks>This function will drop handles it can't duplicate.</remarks>
        public bool IsChildProcessRestricted
        {
            get
            {
                int policy = GetRawMitigationPolicy(ProcessMitigationPolicy.ChildProcess);
                if (policy != 0)
                {
                    return (policy & 1) == 1;
                }

                var result = Query(ProcessInformationClass.ProcessChildProcessInformation, new ProcessChildProcessRestricted(), false);
                if (result.IsSuccess)
                {
                    return result.Result.ProhibitChildProcesses != 0;
                }
                var result_1709 = Query(ProcessInformationClass.ProcessChildProcessInformation, new ProcessChildProcessRestricted1709(), false);
                if (result_1709.IsSuccess)
                {
                    return result_1709.Result.ProhibitChildProcesses != 0;
                }
                return false;
            }
        }

        /// <summary>
        /// Gets whether the process is currently deleting.
        /// </summary>
        public bool IsDeleting
        {
            get { return (ExtendedFlags & ProcessExtendedBasicInformationFlags.IsProcessDeleting) == ProcessExtendedBasicInformationFlags.IsProcessDeleting; }
        }

        /// <summary>
        /// Get process protection information.
        /// </summary>
        public PsProtection Protection
        {
            get
            {
                return Query<PsProtection>(ProcessInformationClass.ProcessProtectionInformation);
            }
        }

        /// <summary>
        /// Query process section image information.
        /// </summary>
        public SectionImageInformation ImageInformation
        {
            get
            {
                return Query<SectionImageInformation>(ProcessInformationClass.ProcessImageInformation);
            }
        }

        /// <summary>
        /// Get full image path name in native format
        /// </summary>
        public override string FullPath
        {
            get
            {
                var result = GetImageFilePath(true, false);
                if (result.IsSuccess)
                {
                    return result.Result;
                }
                if (_pid.HasValue || IsAccessGranted(ProcessAccessRights.QueryLimitedInformation))
                {
                    switch (ProcessId)
                    {
                        case 0:
                            return "Idle";
                        case 4:
                            return "System";
                        default:
                            return $"process:{ProcessId}";
                    }
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// Get owner process ID
        /// </summary>
        public int OwnerProcessId
        {
            get
            {
                return Query<IntPtr>(ProcessInformationClass.ProcessConsoleHostProcess).ToInt32();
            }
        }

        /// <summary>
        /// Query the process token's full package name.
        /// </summary>
        public string PackageFullName => QueryToken(t => t.PackageFullName, string.Empty);

        /// <summary>
        /// Get or set whether resource virtualization is enabled.
        /// </summary>
        public bool VirtualizationEnabled
        {
            get => QueryToken(t => t.VirtualizationEnabled, false);
            set => Set(ProcessInformationClass.ProcessTokenVirtualizationEnabled, value ? 1 : 0);
        }

        #endregion

        #region Static Properties

        /// <summary>
        /// Get the current process.
        /// </summary>
        /// <remarks>This only uses the pseudo handle, for the process. If you need a proper handle use OpenCurrent.</remarks>
        public static NtProcess Current { get => new NtProcess(new SafeKernelObjectHandle(new IntPtr(-1), false)); }

        #endregion
    }
}
