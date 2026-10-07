using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NuGetMirror.Contract.Tests;

/// <summary>
/// Snapshot assertions replacing Verify (removed because Verify releases after
/// 2026-09-01 require a sponsorship license). Snapshots live in a Snapshots/
/// directory next to the test class and are named
/// {TestClassName}.{TestMethodName}.verified.{extension}.
/// On mismatch a *.received.{extension} file is written next to the snapshot
/// and the test fails; rename the received file to *verified* to accept the
/// new output (received files are excluded from source control via .gitignore).
/// </summary>
public static class Snapshots
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void VerifyJson(
        string json,
        [CallerMemberName] string testName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        using var document = JsonDocument.Parse(json);
        string normalized = JsonSerializer.Serialize(document.RootElement, s_indentedJsonOptions);

        string projectDirectory = Directory.GetParent(callerFilePath)!.FullName;
        string className = Path.GetFileNameWithoutExtension(callerFilePath);
        string snapshotBase = Path.Join(projectDirectory, "Snapshots", className + "." + testName);
        string snapshotFileName = snapshotBase + ".verified.json";

        if (!File.Exists(snapshotFileName))
        {
            throw new InvalidOperationException(
                $"Snapshot not found: {snapshotFileName}{Environment.NewLine}" +
                "Create it by copying the expected content into this file and commit it. " +
                "Snapshots are never created automatically.");
        }

        string expected = File.ReadAllText(snapshotFileName)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        if (normalized != expected)
        {
            string receivedFileName = snapshotBase + ".received.json";
            File.WriteAllText(receivedFileName, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.Equal(expected, normalized);
        }
    }
}
