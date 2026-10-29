using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class LobbyAddressSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite(); s.Name = "LobbyAddress";
            s.Tests.Add(new TestCase { Name = "VPN IPv4 and arbitrary mapped port", Run = delegate {
                LobbyAddress a; string error;
                Assert.True(LobbyAddress.TryParse("26.1.2.3:24791", out a, out error), "VPN address accepted");
                Assert.Equal("26.1.2.3", a.Host, "host"); Assert.Equal(24791, a.Port, "mapped TCP port");
            }});
            s.Tests.Add(new TestCase { Name = "FRP hostname and surrounding paste whitespace", Run = delegate {
                LobbyAddress a; string error;
                Assert.True(LobbyAddress.TryParse("  tunnel.example.com:19304  ", out a, out error), "domain accepted");
                Assert.Equal("tunnel.example.com:19304", a.ToString(), "shareable address");
            }});
            s.Tests.Add(new TestCase { Name = "address without port uses lobby default", Run = delegate {
                LobbyAddress a; string error;
                Assert.True(LobbyAddress.TryParse("192.168.1.9", out a, out error), "LAN address");
                Assert.Equal(57000, a.Port, "default lobby port");
            }});
            s.Tests.Add(new TestCase { Name = "invalid pasted addresses never start a connection", Run = delegate {
                foreach (string text in new string[] { "", "host:", "host:0", "host:65536", "host:-1", "host:abc", "host:1:2", "http://host:2", "host/path:2", "user@host:2", "bad host:2", "host\nname:2", "999.1.1.1:2", "[::1]:2" }) {
                    LobbyAddress a; string error;
                    Assert.True(!LobbyAddress.TryParse(text, out a, out error), "refused: " + text);
                    Assert.True(a == null && !string.IsNullOrEmpty(error), "refusal has a message and no endpoint");
                }
            }});
            s.Tests.Add(new TestCase { Name = "port endpoints and localhost", Run = delegate {
                LobbyAddress a; string error;
                Assert.True(LobbyAddress.TryParse("localhost:65535", out a, out error), "upper port allowed");
                Assert.True(LobbyAddress.TryParse("127.0.0.1:1", out a, out error), "lower port allowed");
            }});
            return s;
        }
    }
}
