using DSC.TLink.ITv2.Messages;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests;

/// <summary>
/// The initial burst a panel sends right after it connects is discarded by the session, except the trouble detail
/// notifications (0x0823), which are passed on and marked as coming from the burst.
/// </summary>
public class ConnectBurstTests
{
    [Fact]
    public async Task Only_trouble_details_of_the_burst_are_passed_on_and_they_are_marked()
    {
        var s = await RealSession.StartAsync("S1", waitUntilReady: false);
        try
        {
            s.Transport.Feed(0, new TroubleDetailNotification { Payload = Hex("01-00-01-09-01-00-01") });
            s.Transport.Feed(0, new NonZoneAlarmStatusNotification { Payload = Hex("01-01-03-01") });
            s.Transport.Feed(0, new PartitionAudibleBellNotification { Payload = Hex("01-01-01-02-02-00-F0") });
            s.Transport.Feed(0, new PartitionAlarmMemoryNotification { Payload = Hex("01-01-01") });
            s.Transport.Feed(0, new MultipleMessagePacket
            {
                Messages = new IMessageData[]
                {
                    new NonZoneAlarmStatusNotification { Payload = Hex("01-01-09-01") },
                    new TroubleDetailNotification { Payload = Hex("01-12-01-81-01-01-01") },
                }
            });
            s.Transport.Feed(0, new NotificationArmDisarm { Partition = 1, ArmMode = DSC.TLink.ITv2.Enumerations.ArmingMode.Disarm, UserId = 1 });
            await s.WaitReadyAsync();

            // After the burst: everything is passed on, unmarked.
            s.Transport.Feed(0, new TroubleDetailNotification { Payload = Hex("01-00-01-09-01-00-00") });
            s.Transport.Feed(0, new NonZoneAlarmStatusNotification { Payload = Hex("01-01-03-01") });

            var received = new List<IMessageData>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var m in s.Session.GetNotificationsAsync(cts.Token))
            {
                received.Add(m);
                if (received.Count == 4) break;
            }

            Assert.Collection(received,
                m => Assert.Equal(("01000109010001", true), Burst(m)),
                m => Assert.Equal(("01120181010101", true), Burst(m)),
                m => Assert.Equal(("01000109010000", false), Burst(m)),
                m => Assert.Equal(Hex("01-01-03-01"), Assert.IsType<NonZoneAlarmStatusNotification>(m).Payload));
        }
        finally
        {
            await s.Session.DisposeAsync();
        }

        static (string, bool) Burst(IMessageData m)
        {
            var t = Assert.IsType<TroubleDetailNotification>(m);
            return (Convert.ToHexString(t.Payload), t.ReceivedInConnectBurst);
        }
    }
}
