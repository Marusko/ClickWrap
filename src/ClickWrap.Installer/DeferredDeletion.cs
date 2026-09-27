using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClickWrap.Installer;

/// <summary>
/// Deletes files and folders once this process has exited. Needed because the uninstaller is
/// usually update.exe inside the very folder it removes, and Windows locks a running exe.
/// </summary>
/// <remarks>
/// A hidden cmd.exe retries once a second for up to a minute, which is ample for this process to
/// exit, then gives up; whatever is still locked by then stays behind.
/// Paths reach cmd through environment variables rather than the command line, so a path with
/// spaces, &amp; or non-ASCII characters needs no quoting rules of its own.
/// </remarks>
public sealed class DeferredDeletion
{
    private enum Kind
    {
        Folder,
        File,
        FolderIfEmpty,
    }

    private readonly List<(string Path, Kind Kind)> _targets = [];

    public void AddFolder(string path) => _targets.Add((path, Kind.Folder));

    public void AddFile(string path) => _targets.Add((path, Kind.File));

    /// <summary>Removed only if nothing is left in it, e.g. a shared parent folder.</summary>
    public void AddFolderIfEmpty(string path) => _targets.Add((path, Kind.FolderIfEmpty));

    /// <summary>Starts the background deletion. Call as the very last thing before exiting.</summary>
    public void Start()
    {
        if (!_targets.Any(t => t.Kind != Kind.FolderIfEmpty))
        {
            return;
        }

        // Full paths throughout: a bare "cmd.exe" is looked for beside this exe first, and a bare
        // "ping" in cmd's working directory (%TEMP%), where anyone could leave a copy.
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Anywhere but a folder being deleted, which cmd would otherwise hold open.
            WorkingDirectory = Path.GetTempPath(),
        };

        startInfo.Environment["CLICKWRAP_PING"] = Path.Combine(Environment.SystemDirectory, "PING.EXE");

        var deletes = new StringBuilder();
        var stillThere = new StringBuilder();

        for (var i = 0; i < _targets.Count; i++)
        {
            var (path, kind) = _targets[i];
            var variable = $"CLICKWRAP_DELETE_{i}";
            startInfo.Environment[variable] = Path.TrimEndingDirectorySeparator(path);

            // A trailing backslash makes "if exist" match folders only.
            deletes.Append(kind switch
            {
                Kind.Folder => $"(if exist \"%{variable}%\\\" rd /s /q \"%{variable}%\") & ",
                Kind.File => $"(if exist \"%{variable}%\" del /f /q \"%{variable}%\") & ",
                _ => $"(rd \"%{variable}%\") & ",
            });

            if (kind != Kind.FolderIfEmpty)
            {
                stillThere.Append(kind == Kind.Folder ? $"if not exist \"%{variable}%\\\" " : $"if not exist \"%{variable}%\" ");
            }
        }

        // ping is the sleep: timeout.exe refuses to run without a console to read from.
        var command =
            "for /l %i in (1,1,60) do @(" +
            "\"%CLICKWRAP_PING%\" -n 2 127.0.0.1 >nul & " +
            deletes +
            stillThere + "exit" +
            ") 2>nul";

        // /s: strip exactly the outer quotes, leaving the inner ones alone.
        startInfo.Arguments = $"/d /q /s /c \"{command}\"";

        try
        {
            using var _ = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The app is uninstalled already; the folder just stays behind.
        }
    }
}
