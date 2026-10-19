namespace Athar.Web.Services;

/// <summary>
/// The page tour on screen now (T152), one per circuit. A page's <c>PageTour</c> registers itself here so the top bar's
/// help button can replay it; pages without a tour leave it empty and the button hides.
/// </summary>
public sealed class TourService
{
    private Func<Task>? start;

    public bool Available => start is not null;
    public event Action? Changed;

    public void Register(Func<Task> starter)
    {
        start = starter;
        Changed?.Invoke();
    }

    public void Unregister(Func<Task> starter)
    {
        if (start != starter) return;
        start = null;
        Changed?.Invoke();
    }

    public Task StartAsync() => start?.Invoke() ?? Task.CompletedTask;
}
