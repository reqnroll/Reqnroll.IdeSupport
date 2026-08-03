using Reqnroll;

namespace Corpus.Bindings;

[Binding]
public class CorpusSteps
{
    [Given(@"precondition (\d+) is met")]
    public void GivenPreconditionIsMet(int n) { }

    [When(@"action (\d+) is performed")]
    public void WhenActionIsPerformed(int n) { }

    // Overlapping patterns: a numeric result matches both → ambiguous.
    [Then(@"the result is (.+)")]
    public void ThenTheResultIsText(string s) { }

    [Then(@"the result is (\d+)")]
    public void ThenTheResultIsNumber(int n) { }

    [When(@"feature 0 is enabled")]
    public void WhenFeature0IsEnabled() { }

    [When(@"feature 1 is enabled")]
    public void WhenFeature1IsEnabled() { }

    [When(@"feature 2 is enabled")]
    public void WhenFeature2IsEnabled() { }

    [When(@"feature 3 is enabled")]
    public void WhenFeature3IsEnabled() { }

    [When(@"feature 4 is enabled")]
    public void WhenFeature4IsEnabled() { }

    [When(@"feature 5 is enabled")]
    public void WhenFeature5IsEnabled() { }

    [When(@"feature 6 is enabled")]
    public void WhenFeature6IsEnabled() { }

    [When(@"feature 7 is enabled")]
    public void WhenFeature7IsEnabled() { }

    [When(@"feature 8 is enabled")]
    public void WhenFeature8IsEnabled() { }

    [When(@"feature 9 is enabled")]
    public void WhenFeature9IsEnabled() { }

    [When(@"feature 10 is enabled")]
    public void WhenFeature10IsEnabled() { }

    [When(@"feature 11 is enabled")]
    public void WhenFeature11IsEnabled() { }

    [When(@"feature 12 is enabled")]
    public void WhenFeature12IsEnabled() { }

    [When(@"feature 13 is enabled")]
    public void WhenFeature13IsEnabled() { }

    [When(@"feature 14 is enabled")]
    public void WhenFeature14IsEnabled() { }

    [When(@"feature 15 is enabled")]
    public void WhenFeature15IsEnabled() { }

    [When(@"feature 16 is enabled")]
    public void WhenFeature16IsEnabled() { }

    [When(@"feature 17 is enabled")]
    public void WhenFeature17IsEnabled() { }

    [When(@"feature 18 is enabled")]
    public void WhenFeature18IsEnabled() { }

    [When(@"feature 19 is enabled")]
    public void WhenFeature19IsEnabled() { }

    [When(@"feature 20 is enabled")]
    public void WhenFeature20IsEnabled() { }

    [When(@"feature 21 is enabled")]
    public void WhenFeature21IsEnabled() { }

    [When(@"feature 22 is enabled")]
    public void WhenFeature22IsEnabled() { }

    [When(@"feature 23 is enabled")]
    public void WhenFeature23IsEnabled() { }

    [When(@"feature 24 is enabled")]
    public void WhenFeature24IsEnabled() { }

    [When(@"feature 25 is enabled")]
    public void WhenFeature25IsEnabled() { }

    [When(@"feature 26 is enabled")]
    public void WhenFeature26IsEnabled() { }

    [When(@"feature 27 is enabled")]
    public void WhenFeature27IsEnabled() { }

    [When(@"feature 28 is enabled")]
    public void WhenFeature28IsEnabled() { }

    [When(@"feature 29 is enabled")]
    public void WhenFeature29IsEnabled() { }

    [When(@"feature 30 is enabled")]
    public void WhenFeature30IsEnabled() { }

    [When(@"feature 31 is enabled")]
    public void WhenFeature31IsEnabled() { }

    [When(@"feature 32 is enabled")]
    public void WhenFeature32IsEnabled() { }

    [When(@"feature 33 is enabled")]
    public void WhenFeature33IsEnabled() { }

    [When(@"feature 34 is enabled")]
    public void WhenFeature34IsEnabled() { }

    [When(@"feature 35 is enabled")]
    public void WhenFeature35IsEnabled() { }

    [When(@"feature 36 is enabled")]
    public void WhenFeature36IsEnabled() { }

    [When(@"feature 37 is enabled")]
    public void WhenFeature37IsEnabled() { }

    [When(@"feature 38 is enabled")]
    public void WhenFeature38IsEnabled() { }

    [When(@"feature 39 is enabled")]
    public void WhenFeature39IsEnabled() { }

    [When(@"feature 40 is enabled")]
    public void WhenFeature40IsEnabled() { }

    [When(@"feature 41 is enabled")]
    public void WhenFeature41IsEnabled() { }

    [When(@"feature 42 is enabled")]
    public void WhenFeature42IsEnabled() { }

    [When(@"feature 43 is enabled")]
    public void WhenFeature43IsEnabled() { }

    [When(@"feature 44 is enabled")]
    public void WhenFeature44IsEnabled() { }

    [When(@"feature 45 is enabled")]
    public void WhenFeature45IsEnabled() { }

    [When(@"feature 46 is enabled")]
    public void WhenFeature46IsEnabled() { }

    [When(@"feature 47 is enabled")]
    public void WhenFeature47IsEnabled() { }

    [When(@"feature 48 is enabled")]
    public void WhenFeature48IsEnabled() { }

    [When(@"feature 49 is enabled")]
    public void WhenFeature49IsEnabled() { }

    [When(@"feature 50 is enabled")]
    public void WhenFeature50IsEnabled() { }

    [When(@"feature 51 is enabled")]
    public void WhenFeature51IsEnabled() { }

    [When(@"feature 52 is enabled")]
    public void WhenFeature52IsEnabled() { }

    [When(@"feature 53 is enabled")]
    public void WhenFeature53IsEnabled() { }

    [When(@"feature 54 is enabled")]
    public void WhenFeature54IsEnabled() { }

    [When(@"feature 55 is enabled")]
    public void WhenFeature55IsEnabled() { }

    [When(@"feature 56 is enabled")]
    public void WhenFeature56IsEnabled() { }

    [When(@"feature 57 is enabled")]
    public void WhenFeature57IsEnabled() { }

    [When(@"feature 58 is enabled")]
    public void WhenFeature58IsEnabled() { }

    [When(@"feature 59 is enabled")]
    public void WhenFeature59IsEnabled() { }

    [BeforeScenario]
    public void GlobalBeforeScenario() { }

    [AfterScenario]
    public void GlobalAfterScenario() { }

    [BeforeScenario("hookscope")]
    public void ScopedBeforeScenario() { }

    [BeforeStep]
    public void GlobalBeforeStep() { }

    [AfterStep]
    public void GlobalAfterStep() { }

}
