namespace Litos.SoftwareFactory.Setup;

/// <summary>
/// What the background supervisor did, in <c>&lt;data&gt;/logs/launcher-YYYYMMDD.log</c> (§5.3): it has
/// no console, so without this nobody could see that it is waiting for Docker or why the host
/// would not start. The host writes its own log beside it (FACTORY_LOG_DIR).
/// </summary>
public sealed class LauncherLog(string? directory, TextWriter? echo = null)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}";
        lock (_gate)
        {
            echo?.WriteLine(line);
            if (directory is null)
                return;
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log that cannot be written must not stop the factory.
            }
        }
    }
}
