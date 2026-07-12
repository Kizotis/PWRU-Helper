using System.Windows.Input;
using PWRUHelper;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>The Translator input is multi-line, so what Enter does there is a real decision:
/// translate (like the compact overlay's reply box, and like the game's own chat) rather than
/// insert a line break. Shift+Enter keeps the line break for the rare multi-line message.</summary>
public class TranslateKeyTests
{
    [Theory]
    [InlineData(Key.Enter, ModifierKeys.None, true)]      // the plain press — translates
    [InlineData(Key.Enter, ModifierKeys.Control, true)]   // the old shortcut still works
    [InlineData(Key.Enter, ModifierKeys.Shift, false)]    // deliberate new line
    [InlineData(Key.A, ModifierKeys.None, false)]         // just typing
    [InlineData(Key.Tab, ModifierKeys.None, false)]
    public void Enter_translates_unless_Shift_asks_for_a_new_line(Key key, ModifierKeys modifiers, bool translates)
        => Assert.Equal(translates, MainWindow.IsTranslateKey(key, modifiers));
}
