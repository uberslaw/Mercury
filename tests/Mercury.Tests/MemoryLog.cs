namespace Mercury.Tests;

internal sealed class MemoryLog : IJobLog
{
    public List<LogEvent> Lines { get; } = [];

    public event Action<LogEvent>? LineWritten;

    public void Info(string jobId, string jobName, string message) =>
        Add(jobId, jobName, "Info", message);

    public void Error(string jobId, string jobName, string message) =>
        Add(jobId, jobName, "Error", message);

    private void Add(string jobId, string jobName, string level, string message)
    {
        var ev = new LogEvent { JobId = jobId, JobName = jobName, Level = level, Message = message };
        Lines.Add(ev);
        LineWritten?.Invoke(ev);
    }
}
