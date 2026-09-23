using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

// Ported from Reqnroll.VisualStudio\UI\ViewModels\WelcomeDialogViewModel.cs
namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels;

/// <summary>View model for the first-install welcome dialog, presenting welcome, troubleshooting and community pages.</summary>
public class WelcomeDialogViewModel : WizardViewModel
{
    /// <summary>Markdown text for the welcome page.</summary>
    public const string WELCOME_TEXT = """
        # Welcome to Reqnroll

        Reqnroll for Visual Studio includes a number of features that make it easier work with Reqnroll and SpecFlow projects.

        Here are the most important features supported currently:

        * **Support Reqnroll** (all versions) and **SpecFlow** (from v3)
        * **Gherkin syntax coloring and keyword completion** with new colors, context-sensitive keyword completion and highlighting syntax errors
        * **Step definition matching** with *Navigate to step definition*, *Find step definition usages* and *Define Step wizard* (you need to build the project)
        * **Add new Reqnroll project** and **Add new feature file** templates
        """;

    /// <summary>Markdown text for the troubleshooting page.</summary>
    public const string TROUBLESHOOTING_TEXT = """
        # Troubleshooting Tips

        Reqnroll for Visual Studio 2022 & 2026 is still new, so there might be some glitches, but **we are eager to hear about your feedback**.

        * If you are in trouble, you should first check the **Reqnroll pane of the Output Window**. 
        You can open it by choosing *View / Output* from the Visual Studio menu and switch 
        the *Show output* from dropdown to *Reqnroll*.

        * You can find even more trace information in the **log file** in the [%LOCALAPPDATA%\Reqnroll\logs](file://%LOCALAPPDATA%\Reqnroll\logs) folder.

        * Please **submit your issues** in our [issue tracker on GitHub](https://github.com/reqnroll/Reqnroll.VisualStudio/issues) or suggest a new feature [in our Discussion Board](https://go.reqnroll.net/ideas).
        """;

#if DEBUG
    /// <summary>Sample data used by the WPF designer.</summary>
    public static WelcomeDialogViewModel DesignData = new();
#endif

    /// <summary>Creates the view model with the welcome, troubleshooting and community pages.</summary>
    public WelcomeDialogViewModel() : base("Close", "Welcome to Reqnroll",
        new MarkDownWizardPageViewModel("Welcome") { Text = WELCOME_TEXT },
        new MarkDownWizardPageViewModel("Troubleshooting") { Text = TROUBLESHOOTING_TEXT },
        new MarkDownWizardPageViewModel("Community") { Text = UpgradeDialogViewModel.COMMUNITY_INFO_TEXT })
    {
    }
}
