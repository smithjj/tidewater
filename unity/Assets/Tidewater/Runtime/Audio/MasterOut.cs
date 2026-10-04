using UnityEngine;

// The end of the Web Audio graph (SoundScape.js: master gain -> safety limiter -> destination): on the AudioListener's object, where
// OnAudioFilterRead sees the whole mix. The sources play at a quarter of their level (an AudioSource cannot go above 1, the mixer's gains
// can: a close breaking wave is +5 dB) and `gain` brings them back (master volume / HEADROOM). The limiter is a feed-forward compressor with
// the JS node's settings (threshold -4 dB, knee 4 dB, ratio 16, attack 3 ms, release 250 ms); the browser's node also adds make-up gain and
// looks ahead, this one does not.
namespace Tidewater.Audio
{
	public sealed class MasterOut : MonoBehaviour
	{
		public volatile float gain = 1;     // set from the main thread, smoothed here per block
		float cur = 0, env = 0;             // the gain applied so far; the detector's level (linear)
		int rate = 48000;

		const float THRESH_DB = - 4, KNEE_DB = 4, RATIO = 16, ATTACK = 0.003f, RELEASE = 0.25f;

		void Awake() { rate = AudioSettings.outputSampleRate; }

		// compressor curve: the output level (dB) for an input level (dB), soft knee
		static float Curve( float x )
		{
			float over = x - THRESH_DB;
			if ( 2 * over < - KNEE_DB ) return x;
			if ( 2 * over > KNEE_DB ) return THRESH_DB + over / RATIO;
			float k = over + KNEE_DB / 2;
			return x + ( 1 / RATIO - 1 ) * k * k / ( 2 * KNEE_DB );
		}

		void OnAudioFilterRead( float[] data, int channels )
		{
			float target = gain, a = Mathf.Exp( - 1f / ( ATTACK * rate ) ), r = Mathf.Exp( - 1f / ( RELEASE * rate ) );
			int frames = data.Length / channels;
			float step = ( target - cur ) / Mathf.Max( 1, frames );
			for ( int i = 0; i < frames; i ++ )
			{
				cur += step;
				float peak = 0;
				for ( int c = 0; c < channels; c ++ ) { data[ i * channels + c ] *= cur; peak = Mathf.Max( peak, Mathf.Abs( data[ i * channels + c ] ) ); }
				// the level the detector follows: fast up, slow down
				env = peak > env ? a * env + ( 1 - a ) * peak : r * env + ( 1 - r ) * peak;
				float lvl = 20 * Mathf.Log10( Mathf.Max( env, 1e-6f ) );
				float g = Mathf.Pow( 10, ( Curve( lvl ) - lvl ) / 20 );
				if ( g < 1 ) for ( int c = 0; c < channels; c ++ ) data[ i * channels + c ] *= g;
			}
		}
	}
}
