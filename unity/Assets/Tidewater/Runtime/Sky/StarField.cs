using System;
using UnityEngine;

// The night sky's fixed part, from src/sky/Sky.js: skyStars (one star candidate per cell of a cube-face grid, jittered inside it, a power-law magnitude
// distribution, denser along the Milky Way, with the Milky Way's diffuse glow) and the gradient of skyMoonSky (the faint moonlit sky, a little brighter toward
// the horizon). Baked once, in the Editor, into the cubemap the physically based sky shows as its space emission (Tidewater > Bake star cubemap).
//
// What differs from the shader: the JS evaluates the star function per screen pixel, where a star is a ~0.4 px Gaussian that the temporal upscale smears.
// A cubemap texel sampled at its centre would miss most of them, so every star is splatted into the texels around it as a Gaussian no narrower than
// MIN_SIGMA texels, with the energy of the original star kept (a brighter star is wider, as in Sky.js: "bright stars look bigger"). The scintillation, the
// moon's aureole and the sun-height dependence of which stars show (`dark`) are not baked: DayNight scales the whole field by night * dark.
//
// Directions are in the JS (three.js, sim) axes; the cubemap is in Unity world axes (x, y, -z).
namespace Tidewater.Sky
{
	public static class StarField
	{
		public const float STAR_CELLS = 160f;   // cells per half cube face
		public const float STAR_SIGMA = 0.1f;   // star PSF (cells)
		const float MIN_SIGMA = 0.65f;          // texels
		static readonly Vector3 MW = new Vector3( 0.3f, 0.2f, 1f ).normalized; // pole of the Milky Way band

		static float Frac( float x ) => x - Mathf.Floor( x );
		static float Sat( float x ) => Mathf.Clamp01( x );
		static float Smooth( float e0, float e1, float x ) { float t = Mathf.Clamp01( ( x - e0 ) / ( e1 - e0 ) ); return t * t * ( 3 - 2 * t ); }

		// hash without sine (D. Hoskins), skyHash13
		public static float Hash13( float px, float py, float pz )
		{
			float x = Frac( px * 0.1031f ), y = Frac( py * 0.1030f ), z = Frac( pz * 0.0973f );
			float d = x * ( y + 33.33f ) + y * ( z + 33.33f ) + z * ( x + 33.33f );
			x += d; y += d; z += d;
			return Frac( ( x + y ) * z );
		}

		// the diffuse glow of the Milky Way and the moonlit sky for a direction (sim axes), for the unit night; `dark` = 1
		public static Vector3 Glow( Vector3 dir )
		{
			float bx = Vector3.Dot( dir, MW ) * 4f;
			float band = Mathf.Exp( - bx * bx );
			var mw = new Vector3( 0.55f, 0.6f, 0.75f ) * ( band * 0.0035f ) * Smooth( 0f, 0.2f, dir.y );
			// skyMoonSky without the aureole: vec3( 0.005, 0.0068, 0.0105 ) * grad
			float grad = Mathf.Lerp( 1.7f, 1.0f, Sat( dir.y * 3f ) );
			return mw + new Vector3( 0.005f, 0.0068f, 0.0105f ) * grad;
		}

		// every star of the sky: its direction (sim axes) and linear colour (the energy of the splat, relative to the pixel peak at width `size`)
		public struct Star { public Vector3 dir; public Vector3 color; public float size; }

		public static void ForEachStar( Action<Star> emit )
		{
			// the six cube faces of the JS grid: face id = sign + 2 (x), + 5 (y), + 8 (z)
			for ( int axis = 0; axis < 3; axis ++ )
			for ( int sgn = -1; sgn <= 1; sgn += 2 )
			{
				float face = sgn + ( axis == 0 ? 2 : axis == 1 ? 5 : 8 );
				for ( int cu = -( int ) STAR_CELLS; cu < ( int ) STAR_CELLS; cu ++ )
				for ( int cv = -( int ) STAR_CELLS; cv < ( int ) STAR_CELLS; cv ++ )
				{
					float h = Hash13( cu, cv, face );
					// the star's direction (its cell's jittered point) decides the band, as the pixel's direction does in the shader
					float spx = ( cu + Hash13( cu + 3.1f, cv + 3.1f, face + 3.1f ) * 0.4f + 0.3f ) / STAR_CELLS;
					float spy = ( cv + Hash13( cu + 5.7f, cv + 5.7f, face + 5.7f ) * 0.4f + 0.3f ) / STAR_CELLS;
					Vector3 sdir = axis == 0 ? new Vector3( sgn, spx, spy ) : axis == 1 ? new Vector3( spx, sgn, spy ) : new Vector3( spx, spy, sgn );
					sdir.Normalize();
					float bx = Vector3.Dot( sdir, MW ) * 4f;
					float band = Mathf.Exp( - bx * bx );
					if ( ! ( h < band * 0.035f + 0.025f ) ) continue;
					float uc = Mathf.Max( Hash13( cu + 7.7f, cv + 7.7f, face + 7.7f ), 2e-4f );
					float m = Mathf.Log( uc, 2f ) * 0.602f + 6.5f;
					float vis = Smooth( m - 0.6f, m + 0.6f, 1.0f * 7.5f - 1.0f ); // dark = 1
					float flux = Mathf.Pow( uc, -0.8f );
					float size = Mathf.Log( flux, 2f ) * 0.08f + 1.0f;
					float t = Hash13( cu + 17f, cv + 17f, face + 17f );
					var col = Vector3.Lerp( new Vector3( 1f, 0.8f, 0.6f ), new Vector3( 0.75f, 0.85f, 1f ), t ) * 0.5f + new Vector3( 0.5f, 0.5f, 0.5f );
					// the pixel peak: psf = 1 / size^2 at the centre; scintillation 1; horizon extinction
					float peak = ( 1f / ( size * size ) ) * flux * vis * 0.0075f * Smooth( 0f, 0.2f, sdir.y );
					emit( new Star { dir = sdir, color = col * peak, size = size } );
				}
			}
		}

		// the Gaussian width of a star in cell units (psf: exp( d^2 / size^2 * -0.5 / sigma^2 )): sigma * size
		public static float WidthCells( float size ) => STAR_SIGMA * size;
	}
}
