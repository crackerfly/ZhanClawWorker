using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ZhanClawControl.Models;

namespace ZhanClawControl.Services;

public sealed class UiStateService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public UiState Load()
    {
        if (TryLoad(AppPaths.UiStateFile, out var state))
        {
            return state;
        }

        // File.Replace keeps the previous valid state here. A torn/corrupt primary
        // file therefore does not silently erase peer notes or the emergency backup.
        return TryLoad(AppPaths.UiStateFile + ".bak", out state) ? state : new UiState();
    }

    public bool Save(UiState state) => Save(state, out _);

    public bool Save(UiState state, out string? error)
    {
        string? tempPath = null;
        try
        {
            RuntimeSecurityService.ValidateSecureDataRootForWrite();
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");
            var json = JsonSerializer.Serialize(state, Options);
            tempPath = Path.Combine(
                AppPaths.DataRoot,
                $".{Path.GetFileName(AppPaths.UiStateFile)}.{Guid.NewGuid():N}.tmp");

            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            RuntimeSecurityService.RejectReparsePoint(tempPath);
            RuntimeSecurityService.ValidateSecureDataRootForWrite();
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");

            if (File.Exists(AppPaths.UiStateFile))
            {
                File.Replace(tempPath, AppPaths.UiStateFile, AppPaths.UiStateFile + ".bak", true);
            }
            else
            {
                File.Move(tempPath, AppPaths.UiStateFile);
            }

            RuntimeSecurityService.ValidateSecureDataRootForWrite();
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile);
            RuntimeSecurityService.RejectReparsePoint(AppPaths.UiStateFile + ".bak");

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup; the committed state is unaffected.
                }
            }
        }
    }

    private static bool TryLoad(string path, out UiState state)
    {
        try
        {
            if (File.Exists(path))
            {
                RuntimeSecurityService.ValidateSecureDataRootForWrite();
                RuntimeSecurityService.RejectReparsePoint(path);
                var json = File.ReadAllText(path, Encoding.UTF8);
                RuntimeSecurityService.RejectReparsePoint(path);
                var parsed = JsonSerializer.Deserialize<UiState>(json);
                if (parsed is not null)
                {
                    state = parsed;
                    return true;
                }
            }
        }
        catch
        {
            // The caller will try the last known-good backup.
        }

        state = new UiState();
        return false;
    }
}
