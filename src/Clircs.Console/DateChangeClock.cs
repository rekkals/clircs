namespace Clircs.ConsoleClient;

internal sealed class DateChangeClock(DateTimeOffset initialTime)
{
    private DateTime _date = initialTime.Date;

    public bool Observe(DateTimeOffset now)
    {
        if (now.Date == _date) return false;
        _date = now.Date;
        return true;
    }
}
