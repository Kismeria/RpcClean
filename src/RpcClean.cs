using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("RpcClean")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

static class RpcClean
{
    const string TaskName = "RpcCleanWatcher";
    const string MutexName = "Local\\RpcCleanWatcherMutex";
    const string DefaultSettings =
        "# modSettings.discordRPC overrides. One key=value per line.\r\n" +
        "# values: true / false / number / text\r\n" +
        "showButtons=false\r\n" +
        "showGitHubButton=false\r\n" +
        "showAlbum=true\r\n";

    static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RpcClean");
    static readonly string InstalledExe = Path.Combine(InstallDir, "RpcClean.exe");
    static readonly string SettingsFile = Path.Combine(InstallDir, "settings.txt");
    static readonly string LogFile = Path.Combine(InstallDir, "rpcclean.log");
    static readonly string YmDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YandexMusic");
    static readonly string ConfigFile = Path.Combine(YmDir, "config.json");

    static readonly object Gate = new object();

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--install";
        try
        {
            switch (mode)
            {
                case "--watch": return Watch();
                case "--apply": Apply(); return 0;
                case "--uninstall": return Uninstall();
                default: return Install();
            }
        }
        catch (Exception ex)
        {
            Log("fatal: " + ex);
            if (mode == "--install" || mode == "--uninstall")
                MessageBox.Show(ex.Message, "RpcClean", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // ---------- install / uninstall ----------

    static int Install()
    {
        Directory.CreateDirectory(InstallDir);

        string self = Process.GetCurrentProcess().MainModule.FileName;
        StopWatchers();
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, InstalledExe, true);

        if (!File.Exists(SettingsFile))
            File.WriteAllText(SettingsFile, DefaultSettings, new UTF8Encoding(false));

        int code = RunHidden("schtasks.exe",
            "/Create /TN " + TaskName + " /TR \"\\\"" + InstalledExe + "\\\" --watch\" /SC ONLOGON /RL LIMITED /F");
        if (code != 0)
            throw new Exception("Не удалось создать задачу автозапуска (schtasks код " + code + ").");

        bool applied = false;
        try { applied = Apply(); } catch (Exception ex) { Log("apply on install: " + ex.Message); }

        Process.Start(new ProcessStartInfo(InstalledExe, "--watch") { UseShellExecute = false, CreateNoWindow = true });

        MessageBox.Show(
            "Готово.\n\n" +
            "Установлено в: " + InstallDir + "\n" +
            "Автозапуск: задача " + TaskName + " (при входе в Windows)\n" +
            (applied ? "Настройки применены сейчас.\n" : "Конфиг Яндекс Музыки пока не найден. Применится сам, когда он появится.\n") +
            "\nНастройки: " + SettingsFile + "\n" +
            "Удалить: запусти этот exe с ключом --uninstall",
            "RpcClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static int Uninstall()
    {
        StopWatchers();
        RunHidden("schtasks.exe", "/Delete /TN " + TaskName + " /F");
        string msg = "Задача и фоновый процесс удалены.\nПапка " + InstallDir + " останется. Удали её вручную, если нужно.\nЗначения в config.json остаются как есть.";
        MessageBox.Show(msg, "RpcClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void StopWatchers()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (Process p in Process.GetProcessesByName("RpcClean"))
        {
            try
            {
                if (p.Id == me) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    static int RunHidden(string file, string arguments)
    {
        ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using (Process p = Process.Start(psi))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    // ---------- watcher ----------

    static int Watch()
    {
        bool created;
        using (Mutex m = new Mutex(true, MutexName, out created))
        {
            if (!created) return 0;

            Directory.CreateDirectory(InstallDir);
            System.Threading.Timer debounce = null;
            debounce = new System.Threading.Timer(delegate { SafeApply(); }, null, Timeout.Infinite, Timeout.Infinite);

            FileSystemWatcher fsw = null;
            Action attach = delegate
            {
                if (fsw != null || !Directory.Exists(YmDir)) return;
                fsw = new FileSystemWatcher(YmDir, "config.json");
                fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                FileSystemEventHandler h = delegate { debounce.Change(900, Timeout.Infinite); };
                fsw.Changed += h;
                fsw.Created += h;
                fsw.Renamed += delegate { debounce.Change(900, Timeout.Infinite); };
                fsw.Error += delegate { try { fsw.Dispose(); } catch { } fsw = null; };
                fsw.EnableRaisingEvents = true;
            };

            SafeApply();
            // periodic pass covers: watcher loss, YM dir created later, settings.txt edits
            while (true)
            {
                attach();
                Thread.Sleep(30000);
                SafeApply();
            }
        }
    }

    static void SafeApply()
    {
        try { Apply(); } catch (Exception ex) { Log("apply: " + ex.Message); }
    }

    // ---------- apply ----------

    static Dictionary<string, object> LoadDesired()
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        string text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile, Encoding.UTF8) : DefaultSettings;
        foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            long n;
            if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) d[key] = true;
            else if (val.Equals("false", StringComparison.OrdinalIgnoreCase)) d[key] = false;
            else if (long.TryParse(val, out n) && n >= int.MinValue && n <= int.MaxValue) d[key] = (int)n;
            else d[key] = val;
        }
        return d;
    }

    static bool SameValue(object a, object b)
    {
        if (a == null || b == null) return a == b;
        if (a is bool || b is bool) return a is bool && b is bool && (bool)a == (bool)b;
        if (a is string || b is string) return string.Equals(a as string, b as string, StringComparison.Ordinal);
        try { return Convert.ToDouble(a) == Convert.ToDouble(b); } catch { return false; }
    }

    // returns true if config exists and is in desired state after the call
    static bool Apply()
    {
        lock (Gate)
        {
            if (!File.Exists(ConfigFile)) return false;

            string json = ReadShared(ConfigFile);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;

            Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return false;

            object ms;
            Dictionary<string, object> modSettings = null;
            if (root.TryGetValue("modSettings", out ms)) modSettings = ms as Dictionary<string, object>;
            if (modSettings == null) return false; // mod has not initialised its store yet

            object rpcObj;
            Dictionary<string, object> rpc = null;
            if (modSettings.TryGetValue("discordRPC", out rpcObj)) rpc = rpcObj as Dictionary<string, object>;
            if (rpc == null) return false;

            bool changed = false;
            foreach (KeyValuePair<string, object> kv in LoadDesired())
            {
                object cur;
                if (!rpc.TryGetValue(kv.Key, out cur) || !SameValue(cur, kv.Value))
                {
                    rpc[kv.Key] = kv.Value;
                    changed = true;
                }
            }
            if (!changed) return true;

            string tmp = ConfigFile + ".rpcclean.tmp";
            File.WriteAllText(tmp, js.Serialize(root), new UTF8Encoding(false));
            try
            {
                File.Replace(tmp, ConfigFile, null);
            }
            catch
            {
                File.Copy(tmp, ConfigFile, true);
                File.Delete(tmp);
            }
            Log("settings re-applied");
            return true;
        }
    }

    static string ReadShared(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(200); }
        }
        throw new IOException("config.json занят другим процессом");
    }

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            FileInfo fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + "\r\n");
        }
        catch { }
    }
}
