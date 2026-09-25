using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.QualityGate.Tests.Harness;

/// <summary>
/// Runs the admin page's script as Jellyfin serves it: the embedded resource is pulled out of the
/// built assembly, written next to a small driver, and executed with node. A minimal
/// <c>document</c> stands in for the one call the pure helpers make (<c>escapeHtml</c>).
/// </summary>
public sealed class ConfigPageScript : IDisposable
{
    private const string DocumentShim =
        "globalThis.document = { createElement: () => ({ _t: '', set textContent(v) { this._t = String(v); }, " +
        "get innerHTML() { return this._t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); } }) };\n";

    private readonly string _dir;

    /// <summary>Initializes a new instance of the <see cref="ConfigPageScript"/> class.</summary>
    public ConfigPageScript()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-page-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "configPage.mjs"), ReadShippedScript());
    }

    /// <summary>Evaluates a JavaScript expression against the page module (bound as <c>page</c>) and returns it as JSON.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The JSON text of the result.</returns>
    public string Json(string expression)
    {
        var driver = "driver-" + Guid.NewGuid().ToString("N")[..8] + ".mjs";
        File.WriteAllText(
            Path.Combine(_dir, driver),
            DocumentShim + "const page = await import('./configPage.mjs');\nprocess.stdout.write(JSON.stringify(" + expression + "));\n");

        var startInfo = new ProcessStartInfo("node", driver)
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("node is required to run the config page tests. Install Node.js and put it on PATH.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"node exited {process.ExitCode} evaluating {expression}:\n{error}");
        }

        return output;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static string ReadShippedScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("configPage.js", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
