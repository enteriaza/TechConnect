using TechConnect.Client.DevCerts;

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0].Trim().ToLowerInvariant();
var rest = args.Skip(1).ToArray();
var confirmTrust = HasFlag(rest, "--confirm-trust");
var assumeYes = HasFlag(rest, "--yes");

return command switch
{
    "provision" => ProvisionCommand.Run(confirmTrust, assumeYes),
    "status" => StatusCommand.Run(),
    "renew" => RenewCommand.Run(assumeYes),
    _ => Unknown(command)
};

static bool IsHelp(string value) =>
    value is "-h" or "--help" or "help" or "/?";

static bool HasFlag(string[] values, string flag) =>
    values.Any(v => string.Equals(v, flag, StringComparison.OrdinalIgnoreCase));

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        TechConnect.Client.DevCerts — persistent development PKI (Windows CurrentUser stores)

        Commands:
          provision [--confirm-trust] [--yes]
              Idempotently create/reuse the development CA, WSS server, and client certificates.
              Does not remove certificates. Does not silently rotate.
              Installing the CA into CurrentUser\Root requires --confirm-trust
              (and interactive YES unless --yes is also supplied).

          status
              Read-only diagnostics. Never modifies certificate stores.

          renew [--yes]
              Explicitly issue replacement server/client leaves. Does not remove old certificates.
              Does not replace the CA.

        Builds, tests, and TechConnect.Client application startup never modify the Windows certificate store.
        """);
}
