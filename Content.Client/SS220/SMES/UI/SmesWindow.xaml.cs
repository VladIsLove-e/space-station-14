// © SS220, An EULA/CLA with a hosting restriction, full text: https://raw.githubusercontent.com/SerbiaStrong-220/space-station-14/master/CLA.txt

using Content.Client.Message;
using Content.Shared.SMES;
using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.XAML;

namespace Content.Client.SS220.SMES.UI;

[GenerateTypedNameReferences]
public sealed partial class SmesWindow : DefaultWindow
{
    public SmesWindow()
    {
        RobustXamlLoader.Load(this);
    }

    public void UpdateState(SmesState state)
    {
        Title = state.EntityName;
        DeviceNetworkAddress.Text = Loc.GetString("device-address-examine-message", ("address", state.DeviceNetworkAddress));

        SetBatteryMarkup(BatteryCurrentCharge,
            "examinable-battery-component-current-charge-detail",
            state.BatteryCurrentCharge,
            "currentCharge");

        SetBatteryMarkup(
            BatteryMaxCharge,
            "examinable-battery-component-max-charge-detail",
            state.BatteryMaxCharge,
            "maxCharge");

        SetBatteryMarkup(
            BatteryChargePercent,
            "examinable-battery-component-examine-detail",
            state.BatteryChargePercentRounded,
            "percent");
    }

    private void SetBatteryMarkup(RichTextLabel label, string messageKey, int value, string valueKey)
    {
        label.SetMarkup(Loc.GetString($"{messageKey}", (valueKey, value), ("markupPercentColor", "green")));
    }
}
