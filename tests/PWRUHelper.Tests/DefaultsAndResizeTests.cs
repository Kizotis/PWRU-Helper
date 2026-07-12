using PWRUHelper;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class FirstLaunchDefaultsTests
{
    // The values a brand-new install starts with (no settings.json yet → new AppSettings()).
    [Fact]
    public void First_launch_defaults_match_the_tuned_values()
    {
        var s = new AppSettings();

        Assert.Equal(5, s.SensitivityPercent);      // OCR sensitivity 5%
        Assert.Equal(92, s.LiveSpeedPercent);       // live speed → 0.7s between reads (see below)
        Assert.Equal(2, s.MinFragmentLetters);      // smallest text fragment
        Assert.Equal(60, s.StabilityPercent);       // stability 60%
        Assert.Equal("contrast", s.OcrFilterMode);  // background filter = boost contrast
        Assert.Equal("gdi", s.CaptureBackend);      // capture method = GDI
    }

    [Fact]
    public void The_default_live_speed_really_is_0_7_seconds_between_reads()
    {
        // The setting is a percentage, the thing that matters is the interval it produces. Pin them
        // together: a change to either the default or the slider's mapping now has to be deliberate.
        Assert.Equal(700, MainWindow.LiveIntervalMs(new AppSettings().LiveSpeedPercent));

        // The ends of the slider, so the mapping itself can't drift.
        Assert.Equal(3000, MainWindow.LiveIntervalMs(0));
        Assert.Equal(500, MainWindow.LiveIntervalMs(100));
    }
}

public class SettingsMigrationTests
{
    // The current schema version. Bumping it in SettingsService means adding a step below.
    private const int Current = 3;

    [Fact]
    public void Existing_off_filter_is_upgraded_to_contrast_once()
    {
        var s = new AppSettings { SettingsVersion = 0, OcrFilterMode = "off" };

        Assert.True(SettingsService.Migrate(s));      // changed → caller persists
        Assert.Equal("contrast", s.OcrFilterMode);
        Assert.Equal(Current, s.SettingsVersion);
    }

    [Fact]
    public void A_deliberately_chosen_filter_is_not_touched_by_the_migration()
    {
        var s = new AppSettings { SettingsVersion = 0, OcrFilterMode = "color" };

        Assert.True(SettingsService.Migrate(s));      // still stamps the version
        Assert.Equal("color", s.OcrFilterMode);
        Assert.Equal(Current, s.SettingsVersion);
    }

    [Fact]
    public void The_v1_users_whose_filter_was_clobbered_back_to_off_get_contrast_again()
    {
        // v1 set "contrast" — and then every launch wrote "off" straight back over it (a XAML-load
        // ValueChanged persisted the not-yet-restored combo; fixed in v0.13.0). So a v1 file saying
        // "off" proves nothing about what the user wanted. Re-apply the intended default, once.
        var s = new AppSettings { SettingsVersion = 1, OcrFilterMode = "off" };

        Assert.True(SettingsService.Migrate(s));
        Assert.Equal("contrast", s.OcrFilterMode);
        Assert.Equal(Current, s.SettingsVersion);
    }

    [Fact]
    public void Already_migrated_settings_are_left_alone()
    {
        // Now that the clobber is fixed, turning the filter off is a real choice — keep it off.
        var s = new AppSettings { SettingsVersion = Current, OcrFilterMode = "off" };

        Assert.False(SettingsService.Migrate(s));
        Assert.Equal("off", s.OcrFilterMode);
    }

    [Fact]
    public void Someone_who_never_moved_the_live_speed_slider_gets_the_faster_default()
    {
        // A file holding EXACTLY the old default (80 → ~1.0s) is one nobody chose.
        var s = new AppSettings { SettingsVersion = 2, LiveSpeedPercent = 80 };

        Assert.True(SettingsService.Migrate(s));
        Assert.Equal(700, MainWindow.LiveIntervalMs(s.LiveSpeedPercent));
    }

    [Fact]
    public void A_live_speed_the_user_actually_chose_is_never_overridden()
    {
        // Anything but the old default is a deliberate choice — including a SLOWER one, which is
        // exactly what someone on a weak PC would have picked.
        var s = new AppSettings { SettingsVersion = 2, LiveSpeedPercent = 55 };

        SettingsService.Migrate(s);

        Assert.Equal(55, s.LiveSpeedPercent);
    }
}

public class CompactResizeHitTestTests
{
    // Win32 hit-test codes returned to WM_NCHITTEST.
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    // A 100×100 window with an 8-DIP resize band.
    private static int Hit(double x, double y) => CompactOverlay.ResizeHitTest(x, y, 100, 100, 8);

    [Fact]
    public void Interior_is_not_a_resize_zone()
        => Assert.Equal(0, Hit(50, 50));

    [Theory]
    [InlineData(2, 50, HTLEFT)]
    [InlineData(98, 50, HTRIGHT)]
    [InlineData(50, 2, HTTOP)]
    [InlineData(50, 98, HTBOTTOM)]
    public void Edges_map_to_their_side(double x, double y, int expected)
        => Assert.Equal(expected, Hit(x, y));

    [Theory]
    [InlineData(2, 2, HTTOPLEFT)]
    [InlineData(98, 2, HTTOPRIGHT)]
    [InlineData(2, 98, HTBOTTOMLEFT)]
    [InlineData(98, 98, HTBOTTOMRIGHT)]
    public void Corners_map_to_their_diagonal(double x, double y, int expected)
        => Assert.Equal(expected, Hit(x, y));
}
