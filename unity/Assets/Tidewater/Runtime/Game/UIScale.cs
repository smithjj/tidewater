using UnityEngine;

// The web UI's unit and easings (src/ui/ui.css): --tw-u is 0.7px + 0.04vmin, between 1 and 1.25 px, and the sizes of the HUD pieces are multiples of it; the
// motion uses --tw-ease and --tw-ease-io. IMGUI points stand for the CSS px.
namespace Tidewater.Game
{
	public static class UIScale
	{
		public static float U => Mathf.Clamp( 0.7f + 0.0004f * Mathf.Min( Screen.width, Screen.height ), 1f, 1.25f );
		public static float Edge => 16 * U; // --tw-edge

		// cubic-bezier( x1, y1, x2, y2 ) as a CSS timing function
		public static float Bezier( float x1, float y1, float x2, float y2, float x )
		{
			if ( x <= 0 ) return 0; if ( x >= 1 ) return 1;
			float s = x;
			for ( int i = 0; i < 8; i ++ )
			{
				float cx = 3 * x1 * s * ( 1 - s ) * ( 1 - s ) + 3 * x2 * s * s * ( 1 - s ) + s * s * s - x;
				float dx = 3 * x1 * ( 1 - s ) * ( 1 - 3 * s ) + 3 * x2 * s * ( 2 - 3 * s ) + 3 * s * s;
				if ( Mathf.Abs( dx ) < 1e-5f ) break;
				s = Mathf.Clamp01( s - cx / dx );
			}

			return 3 * y1 * s * ( 1 - s ) * ( 1 - s ) + 3 * y2 * s * s * ( 1 - s ) + s * s * s;
		}

		public static float Ease( float x ) => Bezier( 0.2f, 0.8f, 0.2f, 1f, x );   // --tw-ease
		public static float EaseIO( float x ) => Bezier( 0.65f, 0f, 0.35f, 1f, x ); // --tw-ease-io
	}
}
