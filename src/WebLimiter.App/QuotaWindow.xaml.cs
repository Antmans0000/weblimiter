using System.Globalization;
using System.Windows;
using WebLimiter.Core.Model;

namespace WebLimiter.App;

public partial class QuotaWindow : Window
{
    private const long Mo = 1024 * 1024;
    private const long Go = 1024 * Mo;

    public QuotaWindow(string appName, QuotaRule quota)
    {
        InitializeComponent();
        Result = quota.Clone();
        TitleText.Text = "Quota — " + appName;

        EnabledBox.IsChecked = quota.Enabled;
        bool inGo = quota.LimitBytes >= Go && quota.LimitBytes % (Go / 10) == 0;
        UnitGo.IsChecked = inGo;
        UnitMo.IsChecked = !inGo;
        AmountBox.Text = (quota.LimitBytes / (double)(inGo ? Go : Mo)).ToString("0.##", CultureInfo.CurrentCulture);
        PeriodDay.IsChecked = quota.Period == QuotaPeriod.Day;
        PeriodMonth.IsChecked = quota.Period == QuotaPeriod.Month;
        DirTotal.IsChecked = quota.Direction == QuotaDirection.Total;
        DirDown.IsChecked = quota.Direction == QuotaDirection.Download;
        DirUp.IsChecked = quota.Direction == QuotaDirection.Upload;
        ActionBlock.IsChecked = quota.Action == QuotaAction.Block;
        ActionLimit.IsChecked = quota.Action == QuotaAction.Limit;
        ThrottleBox.Text = (quota.ThrottleBytesPerSec / 1024).ToString(CultureInfo.CurrentCulture);
        UpdateEnabled();
    }

    public QuotaRule Result { get; private set; }

    private void UpdateEnabled()
    {
        Fields.IsEnabled = EnabledBox.IsChecked == true;
        Fields.Opacity = Fields.IsEnabled ? 1 : 0.45;
        ThrottleBox.IsEnabled = ActionLimit.IsChecked == true;
    }

    private void EnabledBox_Click(object sender, RoutedEventArgs e) => UpdateEnabled();
    private void Action_Click(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var q = Result.Clone();
        q.Enabled = EnabledBox.IsChecked == true;
        if (q.Enabled)
        {
            if (!double.TryParse(AmountBox.Text.Replace('.', ','), NumberStyles.Float, CultureInfo.GetCultureInfo("fr-FR"), out double amount)
                || amount <= 0)
            {
                ShowError("Indiquez un volume valide (ex. 500 Mo ou 2,5 Go).");
                return;
            }
            if (!long.TryParse(ThrottleBox.Text, out long throttle) || throttle <= 0)
            {
                ShowError("Indiquez une vitesse réduite valide en Ko/s.");
                return;
            }
            q.LimitBytes = (long)(amount * (UnitGo.IsChecked == true ? Go : Mo));
            q.Period = PeriodMonth.IsChecked == true ? QuotaPeriod.Month : QuotaPeriod.Day;
            q.Direction = DirDown.IsChecked == true ? QuotaDirection.Download
                : DirUp.IsChecked == true ? QuotaDirection.Upload : QuotaDirection.Total;
            q.Action = ActionLimit.IsChecked == true ? QuotaAction.Limit : QuotaAction.Block;
            q.ThrottleBytesPerSec = throttle * 1024;
        }
        Result = q;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
