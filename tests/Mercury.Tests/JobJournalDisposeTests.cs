using Mercury;

namespace Mercury.Tests;

public class JobJournalDisposeTests
{
    [Fact]
    public void MarkCopiedAndSaveJobAfterDisposeThrowObjectDisposedNotNre()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-journal-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = new Job
            {
                Id = "job-dispose",
                SourcePath = root,
                DestinationPath = Path.Combine(root, "dest")
            };
            var journal = JobJournal.Create(root, job);
            journal.UpsertFile(new FileRecord
            {
                RelativePath = "a.zip",
                SourcePath = Path.Combine(root, "a.zip"),
                DestPath = Path.Combine(root, "dest", "a.zip"),
                Size = 10,
                LastWriteUtc = DateTime.UtcNow,
                Status = FileCopyStatus.Pending
            });
            journal.Dispose();

            var copied = Record.Exception(() => journal.MarkCopied("a.zip", "abc"));
            Assert.IsType<ObjectDisposedException>(copied);

            var saved = Record.Exception(() => journal.SaveJob(job));
            Assert.IsType<ObjectDisposedException>(saved);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }
}
