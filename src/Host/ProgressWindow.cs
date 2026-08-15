using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jalyro.Convert.Host;

/// <summary>
/// Shown while a batch runs. Deliberately not shown for a single file — see
/// the design document §6.3: silent success is a feature, and a window that
/// flashes up for a 90 ms conversion is worse than no window at all.
/// </summary>
internal sealed class ProgressWindow : Window
{
    private readonly JobQueue _queue;
    private readonly ProgressBar _bar;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly StackPanel _failures;
    private readonly ScrollViewer _failureScroller;
    private readonly Button _action;

    private bool _finished;

    /// <summary>
    /// True once the window has reported completion. A finished window must
    /// not be reused for a new job: its auto-close timer is still pending and
    /// its button reads "Close" rather than "Cancel".
    /// </summary>
    public bool IsFinished => _finished;

    private DispatcherTimer? _closeTimer;
    private IntPtr _handle = IntPtr.Zero;
    private int _filePercent = -1;

    private readonly bool _indeterminate;

    public ProgressWindow(JobQueue queue, JobQueue.QueuedJob job, bool longRunning = false)
    {
        _queue = queue;

        // Progress is counted per FILE. For a single long conversion that means
        // the bar would sit at 0 and then jump to 100 - worse than honest
        // motion. Per-file progress inside an ffmpeg encode needs its
        // -progress output parsed, which is a later phase.
        _indeterminate = longRunning && job.Total <= 1;

        Title = "Jalyro Convert";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;

        _headline = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };

        _detail = new TextBlock
        {
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _bar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = Math.Max(1, job.Total),
            Value = 0,
            IsIndeterminate = _indeterminate,
            Margin = new Thickness(0, 0, 0, 14)
        };

        _failures = new StackPanel();
        _failureScroller = new ScrollViewer
        {
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Content = _failures,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _action = new Button
        {
            Content = "Cancel",
            Width = 100,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _action.Click += OnAction;

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(_headline);
        panel.Children.Add(_detail);
        panel.Children.Add(_bar);
        panel.Children.Add(_failureScroller);
        panel.Children.Add(_action);
        Content = panel;

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            if (_indeterminate)
                TaskbarProgress.SetIndeterminate(_handle);
            else
                TaskbarProgress.SetProgress(_handle, 0, job.Total);
        };

        Closing += (_, e) =>
        {
            // Closing mid-run cancels rather than orphaning the workers.
            if (!_finished)
                _queue.CancelCurrent();
            TaskbarProgress.Clear(_handle);
        };

        Update(job);
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (_finished)
            Close();
        else
            _queue.CancelCurrent();
    }

    /// <summary>
    /// Progress within the file currently converting, from ffmpeg. Turns the
    /// indeterminate bar into a real one once the first update arrives.
    /// </summary>
    public void UpdateFileProgress(int percent)
    {
        _filePercent = percent;

        if (_indeterminate && percent >= 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Maximum = 100;
            _bar.Value = percent;
            _headline.Text = $"Converting… {percent}%";
            TaskbarProgress.SetProgress(_handle, percent, 100);
        }
    }

    public void Update(JobQueue.QueuedJob job)
    {
        // Defensive: a pending auto-close must never fire during a new job.
        _closeTimer?.Stop();
        _closeTimer = null;

        int done = job.Completed;

        if (_indeterminate)
        {
            _headline.Text = _filePercent >= 0
                ? $"Converting… {_filePercent}%"
                : "Converting…";
        }
        else
        {
            _bar.Maximum = Math.Max(1, job.Total);
            _bar.Value = Math.Min(done, job.Total);
            _headline.Text = $"Converting {Math.Min(done + 1, job.Total)} of {job.Total}";
            TaskbarProgress.SetProgress(_handle, done, job.Total);
        }

        _detail.Text = job.Manifest.Paths.Count > 0
            ? Path.GetFileName(job.Manifest.Paths[Math.Min(done, job.Manifest.Paths.Count - 1)])
            : string.Empty;
    }

    public void Complete(JobQueue.QueuedJob job, ConversionService.JobOutcome outcome)
    {
        _finished = true;
        _bar.IsIndeterminate = false;
        _bar.Value = _bar.Maximum;

        int ok = outcome.SucceededCount;
        int bad = outcome.FailedCount;

        if (bad == 0)
        {
            _headline.Text = ok == 1 ? "Converted 1 file" : $"Converted {ok} files";
            _detail.Text = "Done.";
            TaskbarProgress.Clear(_handle);
        }
        else
        {
            _headline.Text = $"Converted {ok}, {bad} failed";
            _detail.Text = "The files below could not be converted.";

            // The taskbar already turned red here but the bar did not, so a
            // full green bar sat directly above the word "failed". The bar
            // stays full - the work did finish - and the colour carries the
            // outcome. Same brush the settings window uses for errors.
            TaskbarProgress.SetError(_handle);
            _bar.Foreground = Brushes.OrangeRed;

            _failures.Children.Clear();
            foreach (ConversionService.FileOutcome f in outcome.Files)
            {
                if (f.Succeeded) continue;

                _failures.Children.Add(new TextBlock
                {
                    Text = $"{Path.GetFileName(f.Input)} — {f.Error}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
            _failureScroller.Visibility = Visibility.Visible;
        }

        _action.Content = "Close";

        // A clean batch closes itself. A batch with failures waits, because the
        // user needs to read what went wrong.
        if (bad == 0)
        {
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer?.Stop();
                _closeTimer = null;
                if (_finished) Close();
            };
            _closeTimer.Start();
        }
    }
}
