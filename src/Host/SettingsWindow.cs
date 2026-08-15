using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jalyro.Convert.Host;

/// <summary>
/// The settings window.
///
/// Deliberately short. Every option here is a decision the user should not
/// have had to make — the defaults are the product, and anything that needs a
/// setting to be usable is a design failure being papered over.
///
/// These six exist because they are the ones people genuinely disagree about.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action<Settings> _onSaved;

    private readonly Slider _jpeg;
    private readonly Slider _webp;
    private readonly Slider _avif;
    private readonly TextBox _emailEdge;
    private readonly TextBox _emailMb;
    private readonly TextBox _discordMb;
    private readonly CheckBox _streamCopy;
    private readonly CheckBox _motw;
    private readonly CheckBox _alwaysProgress;
    private readonly TextBlock _status;

    public SettingsWindow(Settings settings, Action<Settings> onSaved)
    {
        _settings = settings;
        _onSaved = onSaved;

        Title = "Jalyro Convert — Settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

        panel.Children.Add(Section("Image quality"));
        panel.Children.Add(Note(
            "Used when converting from a LOSSLESS source such as PNG or TIFF, " +
            "and when the source quality cannot be determined.\n\n" +
            "Converting from a JPEG instead matches whatever that file was " +
            "saved at, whether that is higher or lower than this. Re-encoding " +
            "an already-compressed photo at a higher setting cannot recover " +
            "detail — it only makes a bigger file that preserves the same " +
            "artifacts.\n\n" +
            "PNG and TIFF output, and PNG to WEBP, are lossless and ignore this."));

        _jpeg = QualitySlider(panel, "JPG", settings.JpegQuality);
        _webp = QualitySlider(panel, "WEBP", settings.WebpQuality);
        _avif = QualitySlider(panel, "AVIF", settings.AvifQuality);

        panel.Children.Add(Section("Size limits"));
        _emailEdge = NumberBox(panel, "Compress for email — image long edge (px)",
                               settings.EmailImageMaxEdge);
        _emailMb = NumberBox(panel, "Compress for email — video limit (MB)",
                             settings.EmailVideoMegabytes);
        _discordMb = NumberBox(panel, "Discord-friendly — video limit (MB)",
                               settings.DiscordMegabytes);

        panel.Children.Add(Section("Behaviour"));

        _streamCopy = Check(panel,
            "Copy video streams when possible instead of re-encoding",
            "MOV to MP4 is usually just a container change. Copying is lossless " +
            "and takes seconds instead of minutes.",
            settings.PreferStreamCopy);

        _motw = Check(panel,
            "Carry the Mark-of-the-Web across to converted files",
            "Without this, converting a downloaded file strips the marker that " +
            "tells Windows it came from the internet.",
            settings.PropagateMarkOfTheWeb);

        _alwaysProgress = Check(panel,
            "Always show the progress window",
            "Off means single quick conversions happen silently.",
            settings.AlwaysShowProgress);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var reset = new Button { Content = "Reset to defaults", Width = 140, Height = 32,
                                 Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 32,
                                  Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save", Width = 90, Height = 32, IsDefault = true };

        reset.Click += (_, _) => LoadFrom(new Settings());
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();

        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = new ScrollViewer
        {
            MaxHeight = 760,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    private void LoadFrom(Settings s)
    {
        _jpeg.Value = s.JpegQuality;
        _webp.Value = s.WebpQuality;
        _avif.Value = s.AvifQuality;
        _emailEdge.Text = s.EmailImageMaxEdge.ToString();
        _emailMb.Text = s.EmailVideoMegabytes.ToString();
        _discordMb.Text = s.DiscordMegabytes.ToString();
        _streamCopy.IsChecked = s.PreferStreamCopy;
        _motw.IsChecked = s.PropagateMarkOfTheWeb;
        _alwaysProgress.IsChecked = s.AlwaysShowProgress;
    }

    private void Save()
    {
        // Build a NEW object rather than mutating the live one. Editing the
        // shared instance first meant a failed save left the process running on
        // values that were never written - and FormatTable was not reapplied,
        // so the same process held two different ideas of the settings.
        var candidate = new Settings
        {
            JpegQuality = (int)_jpeg.Value,
            WebpQuality = (int)_webp.Value,
            AvifQuality = (int)_avif.Value,

            EmailImageMaxEdge = _settings.EmailImageMaxEdge,
            EmailVideoMegabytes = _settings.EmailVideoMegabytes,
            DiscordMegabytes = _settings.DiscordMegabytes,

            PreferStreamCopy = _streamCopy.IsChecked == true,
            PropagateMarkOfTheWeb = _motw.IsChecked == true,
            AlwaysShowProgress = _alwaysProgress.IsChecked == true
        };

        // A hand-typed box can hold anything. Keep the previous value rather
        // than writing nonsense.
        if (int.TryParse(_emailEdge.Text, out int edge)) candidate.EmailImageMaxEdge = edge;
        if (int.TryParse(_emailMb.Text, out int emb))    candidate.EmailVideoMegabytes = emb;
        if (int.TryParse(_discordMb.Text, out int dmb))  candidate.DiscordMegabytes = dmb;

        candidate.Validate();

        if (candidate.Save())
        {
            _onSaved(candidate);
            Close();
        }
        else
        {
            _status.Text = "Could not save. Check that " + Settings.Path + " is writable.";
            _status.Foreground = Brushes.OrangeRed;
        }
    }

    // -- small builders -----------------------------------------------------

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 6)
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static Slider QualitySlider(Panel parent, string label, int value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);

        var slider = new Slider
        {
            Minimum = 40,
            Maximum = 100,
            Value = Math.Clamp(value, 40, 100),
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(slider, 1);

        var readout = new TextBlock
        {
            Text = value.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(readout, 2);
        slider.ValueChanged += (_, e) => readout.Text = ((int)e.NewValue).ToString();

        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(readout);
        parent.Children.Add(row);

        return slider;
    }

    private static TextBox NumberBox(Panel parent, string label, int value)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 6, 0, 3),
            TextWrapping = TextWrapping.Wrap
        });

        var box = new TextBox { Text = value.ToString(), Width = 110,
                                HorizontalAlignment = HorizontalAlignment.Left };
        parent.Children.Add(box);
        return box;
    }

    private static CheckBox Check(Panel parent, string label, string note, bool value)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Margin = new Thickness(0, 10, 0, 0)
        };
        parent.Children.Add(box);
        parent.Children.Add(new TextBlock
        {
            Text = note,
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 2, 0, 0)
        });
        return box;
    }
}
