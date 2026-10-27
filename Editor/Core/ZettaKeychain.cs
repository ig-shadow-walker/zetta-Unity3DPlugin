using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Zetta3D.Editor
{
    /// <summary>
    /// The user's own AI keys, one per provider, in the operating system's credential store — the macOS
    /// Keychain, the Windows Credential Manager, or the Secret Service on Linux.
    ///
    /// NEVER EDITORPREFS. EditorPrefs is a plain-text file every Unity project on the machine reads; a
    /// paid API key does not belong there. The credential store encrypts at rest and is per-user.
    ///
    /// Keys are read when a message is sent and nowhere else; the plugin never shows one again after it is
    /// saved, and never writes one to a log.
    /// </summary>
    public static class ZettaKeychain
    {
        const string Service = "Zetta 3D for Unity";

        /// <summary>Raised when a key is saved or removed, so menus that list providers can rebuild.</summary>
        public static event Action Changed;

        /// <summary>Which providers have a key: known after the first look, so menus don't hit the store on every draw.</summary>
        static readonly Dictionary<string, bool> Known = new Dictionary<string, bool>();

        public static bool Has(string provider)
        {
            if (Known.TryGetValue(provider, out var has)) return has;
            has = !string.IsNullOrEmpty(Get(provider));
            Known[provider] = has;
            return has;
        }

        /// <summary>The saved key, or null. Read only to send a message or check the key.</summary>
        public static string Get(string provider)
        {
            try
            {
                return Store.Read(provider);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zetta 3D] Could not read the saved key from the system key store ({e.GetType().Name}).");
                return null;
            }
        }

        /// <summary>Saves or replaces the key. Returns an error to show, or null.</summary>
        public static string Set(string provider, string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return "Paste a key first.";
            try
            {
                Store.Write(provider, key);
                Known[provider] = true;
                Changed?.Invoke();
                return null;
            }
            catch (Exception e)
            {
                return "Could not save the key in the system key store: " + e.Message;
            }
        }

        public static void Delete(string provider)
        {
            try
            {
                Store.Remove(provider);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zetta 3D] Could not remove the saved key ({e.GetType().Name}).");
            }
            Known[provider] = false;
            Changed?.Invoke();
        }

        /// <summary>Where keys are kept on this machine, for the settings note.</summary>
        public static string StoreName =>
            Application.platform == RuntimePlatform.OSXEditor ? "your macOS Keychain"
            : Application.platform == RuntimePlatform.WindowsEditor ? "Windows Credential Manager"
            : LinuxStore.HasSecretTool ? "your system keyring" : "a private file in your home folder";

        static IStore Store =>
            Application.platform == RuntimePlatform.OSXEditor ? MacStore.Instance
            : Application.platform == RuntimePlatform.WindowsEditor ? WindowsStore.Instance
            : (IStore)LinuxStore.Instance;

        interface IStore
        {
            string Read(string provider);
            void Write(string provider, string key);
            void Remove(string provider);
        }

        // ─── macOS: Keychain Services ───────────────────────────────────────────────

        /// <summary>
        /// The login keychain, through Security.framework directly. Not the `security` tool: that takes
        /// the password on its command line, where any process listing would show it.
        /// </summary>
        sealed class MacStore : IStore
        {
            public static readonly MacStore Instance = new MacStore();
            const string Security = "/System/Library/Frameworks/Security.framework/Security";
            const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            const int NotFound = -25300; // errSecItemNotFound

            [DllImport(Security)]
            static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
                uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);

            [DllImport(Security)]
            static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
                uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr item);

            [DllImport(Security)]
            static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

            [DllImport(Security)]
            static extern int SecKeychainItemDelete(IntPtr item);

            [DllImport(Security)]
            static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

            [DllImport(CoreFoundation)]
            static extern void CFRelease(IntPtr reference);

            static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(Service);

            static int Find(string provider, out byte[] secret, out IntPtr item)
            {
                var account = Encoding.UTF8.GetBytes(provider);
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
                    (uint)account.Length, account, out var length, out var data, out item);
                secret = null;
                if (status != 0) return status;
                secret = new byte[length];
                Marshal.Copy(data, secret, 0, (int)length);
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                return 0;
            }

            public string Read(string provider)
            {
                var status = Find(provider, out var secret, out var item);
                if (status == NotFound) return null;
                Check(status);
                CFRelease(item);
                return Encoding.UTF8.GetString(secret);
            }

            public void Write(string provider, string key)
            {
                var data = Encoding.UTF8.GetBytes(key);
                var status = Find(provider, out _, out var item);
                if (status == 0)
                {
                    try
                    {
                        Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data));
                    }
                    finally
                    {
                        CFRelease(item);
                    }
                    return;
                }
                if (status != NotFound) Check(status);
                var account = Encoding.UTF8.GetBytes(provider);
                Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
                    (uint)account.Length, account, (uint)data.Length, data, out var added));
                if (added != IntPtr.Zero) CFRelease(added);
            }

            public void Remove(string provider)
            {
                var status = Find(provider, out _, out var item);
                if (status == NotFound) return;
                Check(status);
                try
                {
                    Check(SecKeychainItemDelete(item));
                }
                finally
                {
                    CFRelease(item);
                }
            }

            static void Check(int status)
            {
                if (status != 0) throw new InvalidOperationException($"Keychain error {status}.");
            }
        }

        // ─── Windows: Credential Manager ────────────────────────────────────────────

        sealed class WindowsStore : IStore
        {
            public static readonly WindowsStore Instance = new WindowsStore();
            const int Generic = 1; // CRED_TYPE_GENERIC
            const int LocalMachine = 2; // CRED_PERSIST_LOCAL_MACHINE: this user, this machine
            const int NotFound = 1168; // ERROR_NOT_FOUND

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct Credential
            {
                public int Flags;
                public int Type;
                public string TargetName;
                public string Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public int CredentialBlobSize;
                public IntPtr CredentialBlob;
                public int Persist;
                public int AttributeCount;
                public IntPtr Attributes;
                public string TargetAlias;
                public string UserName;
            }

            [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CredWrite(ref Credential credential, int flags);

            [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

            [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CredDelete(string target, int type, int flags);

            [DllImport("advapi32.dll")]
            static extern void CredFree(IntPtr buffer);

            static string Target(string provider) => $"{Service}/{provider}";

            public string Read(string provider)
            {
                if (!CredRead(Target(provider), Generic, 0, out var pointer))
                {
                    if (Marshal.GetLastWin32Error() == NotFound) return null;
                    throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}.");
                }
                try
                {
                    var credential = Marshal.PtrToStructure<Credential>(pointer);
                    var bytes = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CredFree(pointer);
                }
            }

            public void Write(string provider, string key)
            {
                var bytes = Encoding.UTF8.GetBytes(key);
                var blob = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                    var credential = new Credential
                    {
                        Type = Generic,
                        TargetName = Target(provider),
                        CredentialBlobSize = bytes.Length,
                        CredentialBlob = blob,
                        Persist = LocalMachine,
                        UserName = provider,
                    };
                    if (!CredWrite(ref credential, 0))
                        throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}.");
                }
                finally
                {
                    Marshal.FreeHGlobal(blob);
                }
            }

            public void Remove(string provider)
            {
                if (!CredDelete(Target(provider), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
                    throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}.");
            }
        }

        // ─── Linux: Secret Service, or a private file ────────────────────────────────

        /// <summary>
        /// `secret-tool` (libsecret) when it is installed — it reads the secret from standard input, so the
        /// key never appears on a command line. Without it, a file only this user can read.
        /// </summary>
        sealed class LinuxStore : IStore
        {
            public static readonly LinuxStore Instance = new LinuxStore();

            public static bool HasSecretTool =>
                File.Exists("/usr/bin/secret-tool") || File.Exists("/usr/local/bin/secret-tool");

            static string FallbackFile => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "zetta3d", "provider-keys");

            public string Read(string provider)
            {
                if (HasSecretTool)
                {
                    var (code, output) = Run($"lookup service \"{Service}\" provider {provider}", null);
                    return code == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
                }
                return ReadFile().TryGetValue(provider, out var key) ? key : null;
            }

            public void Write(string provider, string key)
            {
                if (HasSecretTool)
                {
                    var (code, _) = Run($"store --label \"{Service} ({provider})\" service \"{Service}\" provider {provider}", key);
                    if (code != 0) throw new InvalidOperationException("secret-tool could not save the key.");
                    return;
                }
                var keys = ReadFile();
                keys[provider] = key;
                WriteFile(keys);
            }

            public void Remove(string provider)
            {
                if (HasSecretTool)
                {
                    Run($"clear service \"{Service}\" provider {provider}", null);
                    return;
                }
                var keys = ReadFile();
                if (keys.Remove(provider)) WriteFile(keys);
            }

            static (int code, string output) Run(string arguments, string input)
            {
                var start = new ProcessStartInfo("secret-tool", arguments)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(start);
                if (input != null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10_000);
                return (process.ExitCode, output);
            }

            static Dictionary<string, string> ReadFile()
            {
                var keys = new Dictionary<string, string>();
                if (!File.Exists(FallbackFile)) return keys;
                foreach (var line in File.ReadAllLines(FallbackFile))
                {
                    var at = line.IndexOf('=');
                    if (at > 0) keys[line.Substring(0, at)] = line.Substring(at + 1);
                }
                return keys;
            }

            static void WriteFile(Dictionary<string, string> keys)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackFile));
                var text = new StringBuilder();
                foreach (var pair in keys) text.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                File.WriteAllText(FallbackFile, text.ToString());
                // Owner read/write only: the same promise a keyring makes, as far as a file can.
                Process.Start("chmod", $"600 \"{FallbackFile}\"")?.WaitForExit(5_000);
            }
        }
    }
}
