using System.Diagnostics;
using System.Windows.Automation;

/// <summary>
/// Drives the real WebLimiter window through UI Automation: opens the Paramètres tab,
/// presses a button by its label and reports the texts the window shows afterwards.
/// Usage: WebLimiter.Smoke ui "path\to\WebLimiter.exe" "Libellé du bouton"
/// </summary>
internal static class UiServiceTest
{
    public static void Run(string exe, string buttonLabel)
    {
        var window = FindWindow() ?? Launch(exe);
        Console.WriteLine("Fenêtre WebLimiter trouvée.");

        Select(window, "Paramètres");
        Thread.Sleep(1500);
        foreach (AutomationElement tab in window.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)))
            Console.WriteLine($"  onglet {tab.Current.Name} sélectionné={((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected}");
        DumpTexts(window, "Avant");
        if (buttonLabel == "-") return; // inspection only

        var button = WaitFor(() => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>()
            .FirstOrDefault(b => b.Current.Name.StartsWith(buttonLabel, StringComparison.OrdinalIgnoreCase)), 5)
            ?? throw new InvalidOperationException($"Bouton « {buttonLabel} » introuvable.");
        Console.WriteLine($"Clic sur « {buttonLabel} »…");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        // The operation runs in the background; wait for the busy message to go away.
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            if (!Texts(window).Any(t => t.Contains("Opération en cours"))) break;
        }
        Thread.Sleep(2500);
        DumpTexts(window, "Après");
    }

    private static AutomationElement? FindWindow() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.NameProperty, "WebLimiter"));

    private static AutomationElement Launch(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return WaitFor(FindWindow, 20) ?? throw new InvalidOperationException("La fenêtre WebLimiter n'est pas apparue.");
    }

    private static void Select(AutomationElement window, string tab)
    {
        var item = WaitFor(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.NameProperty, tab))), 10)
            ?? throw new InvalidOperationException($"Onglet « {tab} » introuvable.");
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    }

    private static List<string> Texts(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(e => e.Current.Name)
            .Where(n => n.Contains("service", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("moteur", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Service", StringComparison.Ordinal)
                        || n.Contains("Filtrage", StringComparison.Ordinal)
                        || n.Contains("Opération", StringComparison.Ordinal))
            .Distinct()
            .ToList();

    private static void DumpTexts(AutomationElement window, string label)
    {
        Console.WriteLine($"--- {label} ---");
        foreach (var t in Texts(window)) Console.WriteLine("  " + t);
        var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Select(b => b.Current.Name)
            .Where(n => n.Contains("service", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("  Boutons : " + string.Join(" | ", buttons));
    }

    private static T? WaitFor<T>(Func<T?> probe, int seconds) where T : class
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            var r = probe();
            if (r != null) return r;
            Thread.Sleep(250);
        }
        return null;
    }
}
