namespace MyBookingGts.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Semaphore _semaphore;
    private readonly bool _acquired;
    private int _disposed;

    private SingleInstanceGuard(Semaphore semaphore, bool acquired)
    {
        _semaphore = semaphore;
        _acquired = acquired;
    }

    public static SingleInstanceGuard Acquire(string name)
    {
        var semaphore = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            name);

        try
        {
            var acquired = semaphore.WaitOne(0);

            if (!acquired)
            {
                throw new InvalidOperationException(
                    "Another My Booking GTS instance is already running.");
            }

            return new SingleInstanceGuard(semaphore, acquired: true);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_acquired)
            {
                _semaphore.Release();
            }
        }
        finally
        {
            _semaphore.Dispose();
        }
    }
}
