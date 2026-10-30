using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private static string ProviderModelLabel(ResolvedModel model)
        => string.IsNullOrWhiteSpace(model.ProviderName) ? model.Name : $"{model.ProviderName}·{model.Name}";

    private void ConfigureModelPopup()
    {
        ModelButton.Click += (_, _) => ModelPopup.IsOpen = !ModelPopup.IsOpen;
        ModelButton.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up)
            {
                ModelPopup.IsOpen = true;
                e.Handled = true;
            }
        };
        ModelPopup.Opened += (_, _) =>
        {
            // Keep the popup inside a narrow sidebar and focus its current native list item.
            ModelPopupBody.Width = Math.Max(160, Math.Min(320, Bounds.Width - 16));
            Dispatcher.UIThread.Post(() =>
            {
                if (!ModelPopup.IsOpen) return;
                if (ProviderList.ContainerFromIndex(ProviderList.SelectedIndex) is { } item)
                {
                    item.Focus();
                    item.BringIntoView();
                }
                else
                {
                    ReasoningCombo.Focus();
                }
            }, DispatcherPriority.Loaded);
        };
        ReasoningCombo.DropDownClosed += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ModelPopup.IsOpen && !ReasoningCombo.IsDropDownOpen) ReasoningCombo.Focus();
            }, DispatcherPriority.Loaded);
        };
        ModelPopupBody.AddHandler(KeyDownEvent, (_, e) =>
        {
            // The reasoning ComboBox consumes its own first Escape while its dropdown is open.
            if (e.Key == Key.Escape && !ReasoningCombo.IsDropDownOpen)
            {
                CloseModelPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ProviderList.IsKeyboardFocusWithin)
            {
                CloseModelPopup();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        ModelPopup.Closed += (_, _) =>
        {
            if (!_detached && IsEffectivelyVisible) ModelButton.Focus();
        };
    }

    private void CloseModelPopup() => ModelPopup.IsOpen = false;
}
