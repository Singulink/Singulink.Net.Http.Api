namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeyAuthenticatorExceptionTests
{
    [TestMethod]
    public void Exception_KeepsReasonMessageAndInnerException()
    {
        var inner = new FormatException();
        var ex = new PasskeyAuthenticatorException(PasskeyErrorReason.Security, "Custom message.", inner);

        ex.Reason.ShouldBe(PasskeyErrorReason.Security);
        ex.Message.ShouldBe("Custom message.");
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [TestMethod]
    public void ReasonOnly_HasDefaultMessage()
    {
        var ex = new PasskeyAuthenticatorException(PasskeyErrorReason.NotSupported);

        ex.Reason.ShouldBe(PasskeyErrorReason.NotSupported);
        ex.Message.ShouldBe(new PasskeyAuthenticatorException(PasskeyErrorReason.NotSupported, null).Message);
        ex.InnerException.ShouldBeNull();
    }

    [TestMethod]
    public void Exception_HasDistinctDefaultMessages()
    {
        var messages = Enum.GetValues(typeof(PasskeyErrorReason)).Cast<PasskeyErrorReason>()
            .Select(r => new PasskeyAuthenticatorException(r, null).Message)
            .ToList();

        messages.ShouldAllBe(m => m.Length > 0);
        messages.Distinct().Count().ShouldBe(messages.Count);
    }

    [TestMethod]
    [DataRow(PasskeyErrorReason.Cancelled, true)]
    [DataRow(PasskeyErrorReason.NotAllowed, true)]
    [DataRow(PasskeyErrorReason.Unknown, false)]
    [DataRow(PasskeyErrorReason.AlreadyOnAuthenticator, false)]
    [DataRow(PasskeyErrorReason.NotSupported, false)]
    [DataRow(PasskeyErrorReason.RequirementsNotMet, false)]
    [DataRow(PasskeyErrorReason.Security, false)]
    [DataRow(PasskeyErrorReason.PageNotOpened, false)]
    [DataRow(PasskeyErrorReason.TimedOut, false)]
    public void IsCancellation_IsTrueForCancelledAndNotAllowed(PasskeyErrorReason reason, bool isCancellation)
    {
        new PasskeyAuthenticatorException(reason, null).IsCancellation.ShouldBe(isCancellation);
    }
}
