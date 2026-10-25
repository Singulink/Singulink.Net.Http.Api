using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The sign-in services wired to a short-lived store that runs on a <see cref="TestClock"/>. With a <see cref="StoreKind.Shared"/> store, every access to
/// <see cref="Codes"/> or <see cref="Links"/> returns a new instance that shares nothing with the others but the store, as if each call ran on a different
/// server.
/// </summary>
public sealed class SignInTestContext
{
    private readonly IOptions<SignInCodeOptions> _codeOptions;
    private readonly IOptions<DeviceLinkOptions> _linkOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SignInCodes? _codes;
    private readonly DeviceLinks? _links;

    public SignInTestContext(
        StoreKind storeKind = StoreKind.Memory,
        Action<SignInCodeOptions>? configureCodes = null,
        Action<DeviceLinkOptions>? configureLinks = null,
        Func<IShortLivedStore, IShortLivedStore>? wrapStore = null)
    {
        var codeOptions = new SignInCodeOptions();
        configureCodes?.Invoke(codeOptions);
        _codeOptions = Options.Create(codeOptions);

        var linkOptions = new DeviceLinkOptions();
        configureLinks?.Invoke(linkOptions);
        _linkOptions = Options.Create(linkOptions);

        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs));

        StoreKind = storeKind;
        IShortLivedStore store = storeKind is StoreKind.Memory ? new MemoryShortLivedStore(Clock) : new SharedShortLivedStore(Clock);
        Store = wrapStore?.Invoke(store) ?? store;

        if (storeKind is StoreKind.Memory)
        {
            _codes = CreateCodes();
            _links = CreateLinks(Clock);
        }
    }

    public StoreKind StoreKind { get; }

    public TestClock Clock { get; } = new();

    public IShortLivedStore Store { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public SignInCodes Codes => _codes ?? CreateCodes();

    public DeviceLinks Links => _links ?? CreateLinks(Clock);

    /// <summary>
    /// Creates the device links service of a server whose clock is the specified one, sharing the store with the others.
    /// </summary>
    public DeviceLinks CreateLinks(TimeProvider clock) => new(Store, CreateCodes(), _linkOptions, clock, _loggerFactory.CreateLogger<DeviceLinks>());

    private SignInCodes CreateCodes() => new(Store, _codeOptions, _loggerFactory.CreateLogger<SignInCodes>());
}
