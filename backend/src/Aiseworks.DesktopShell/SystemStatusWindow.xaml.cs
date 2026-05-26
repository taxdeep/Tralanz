using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aiseworks.DesktopShell;

public partial class SystemStatusWindow : Window
{
    public SystemStatusWindow(SystemStatusSnapshot snapshot)
    {
        InitializeComponent();

        ProfileNameTextBlock.Text = snapshot.ProfileName;
        ProfileDescriptionTextBlock.Text = snapshot.ProfileDescription;
        ModeTextBlock.Text = snapshot.Mode;
        LastCheckedTextBlock.Text = $"Last checked: {snapshot.LastChecked}";
        HealthSummaryTextBlock.Text = snapshot.HealthSummary;
        ApplyStatusBrush(HealthSummaryTextBlock, snapshot.HealthSummary);

        SetStatus(ServiceStatusTextBlock, snapshot.ServiceStatus);
        ServiceTargetTextBlock.Text = snapshot.ServiceTarget;
        ServiceDetailTextBlock.Text = $"Service detail: {snapshot.ServiceDetail}";

        SetStatus(PostgresStatusTextBlock, snapshot.PostgresStatus);
        PostgresTargetTextBlock.Text = snapshot.PostgresTarget;

        SetStatus(AccountingStatusTextBlock, snapshot.AccountingStatus);
        AccountingTargetTextBlock.Text = snapshot.AccountingTarget;

        SetStatus(BusinessStatusTextBlock, snapshot.BusinessStatus);
        BusinessTargetTextBlock.Text = snapshot.BusinessTarget;

        SetStatus(SysAdminApiStatusTextBlock, snapshot.SysAdminApiStatus);
        SysAdminApiTargetTextBlock.Text = snapshot.SysAdminApiTarget;

        SetStatus(SysAdminStatusTextBlock, snapshot.SysAdminStatus);
        SysAdminTargetTextBlock.Text = snapshot.SysAdminTarget;
    }

    private static void SetStatus(TextBlock target, string status)
    {
        target.Text = status;
        ApplyStatusBrush(target, status);
    }

    private static void ApplyStatusBrush(TextBlock target, string status)
    {
        target.Foreground = status switch
        {
            "Online" or "Running" => new SolidColorBrush(Color.FromRgb(5, 122, 85)),
            "N/A" or "Unknown" or "Not checked" => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            _ => new SolidColorBrush(Color.FromRgb(185, 28, 28))
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public sealed record SystemStatusSnapshot(
    string ProfileName,
    string ProfileDescription,
    string Mode,
    string ServiceStatus,
    string ServiceTarget,
    string ServiceDetail,
    string PostgresStatus,
    string PostgresTarget,
    string AccountingStatus,
    string AccountingTarget,
    string BusinessStatus,
    string BusinessTarget,
    string SysAdminApiStatus,
    string SysAdminApiTarget,
    string SysAdminStatus,
    string SysAdminTarget,
    string LastChecked,
    string HealthSummary);
