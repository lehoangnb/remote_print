namespace RemotePrint.Client;

public static class TestPrinter
{
    public const string Name = "Remote Print Test";
    // Deliberately outside RP_: this is a local destination, not a relayed queue.
    public const string PortName = "RemotePrint_Test_LPR";
    public const string QueueId = "remoteprint-test";
    public const string DriverName = "Microsoft Print To PDF";
    public const string LegacyDriverName = "Generic / Text Only";
}
