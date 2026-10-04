using Tidewater.Game;
using Tidewater.Player;
using Vector3 = Tidewater.Engine.Vector3;

// The game's sound hooks (IGameAudio) forwarded to whichever SoundScape the PlayerHost has now: the SoundHost is rebuilt when the Editor
// destroys it, the rod is not, and every hook is optional (nothing happens without a sound).
namespace Tidewater.Audio
{
	public sealed class GameAudioProxy : IGameAudio
	{
		readonly PlayerHost host;
		public GameAudioProxy( PlayerHost host ) { this.host = host; }
		IGameAudio a => host != null && host.sound != null && host.sound.Alive ? host.sound.scape : null;

		public void rodReady() => a?.rodReady();
		public void bail( bool open ) => a?.bail( open );
		public void rodLoop( double crankRate, double lineOutRate, double tension ) => a?.rodLoop( crankRate, lineOutRate, tension );
		public void whoosh( double power ) => a?.whoosh( power );
		public void lineOut( double power ) => a?.lineOut( power );
		public void plop( Vector3 at ) => a?.plop( at );
		public void fishSplash( Vector3 at, double v ) => a?.fishSplash( at, v );
		public void fishFlop() => a?.fishFlop();
		public void lineSnap() => a?.lineSnap();
		public void splash( double v ) => a?.splash( v );
		public void coin() => a?.coin();
	}
}
