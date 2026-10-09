namespace Neruna.Client.Tests;

/// <summary>
/// Tests against the test lab (GreenMail, Radicale) share its accounts (anna, lea, marco): they run one after the other,
/// so one test's mails and calendar changes never show up in another's expectations.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Testlab
{
    public const string Name = "Testlab";
}
