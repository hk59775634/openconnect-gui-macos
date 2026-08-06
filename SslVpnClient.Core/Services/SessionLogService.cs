using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SslVpnClient.Abstractions;

namespace SslVpnClient.Services;

public sealed partial class SessionLogService : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private int _flushScheduled;

    public ObservableCollection<string> Entries { get; } = new();

    [ObservableProperty]
    private string _text = string.Empty;

    public SessionLogService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Clear();
    }

    public void Append(string entry)
    {
        void Add()
        {
            lock (_sync)
            {
                Entries.Add(entry);
                while (Entries.Count > 1000)
                {
                    Entries.RemoveAt(0);
                }
            }

            ScheduleFlush();
        }

        if (_dispatcher.CheckAccess())
        {
            Add();
        }
        else
        {
            _dispatcher.Post(Add);
        }
    }

    public void Clear()
    {
        void DoClear()
        {
            lock (_sync)
            {
                Entries.Clear();
                Text = string.Empty;
            }
        }

        if (_dispatcher.CheckAccess())
        {
            DoClear();
        }
        else
        {
            _dispatcher.Post(DoClear);
        }
    }

    private void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            lock (_sync)
            {
                Text = string.Join(Environment.NewLine, Entries);
            }

            Interlocked.Exchange(ref _flushScheduled, 0);
        });
    }
}
