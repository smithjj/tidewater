using System;
using Tidewater.Engine;

// The numbers behind the day-night sky, as src/App.js (updateSun, applyAtmosphereReadback) and src/sky/Sky.js compute them: where the sun is for a
// clock hour, how far into the night we are, where the moon is, which of them is the key light, and the key light's colour and the night ambient.
// Plain doubles, no Unity types, so the oracle (tools/dump-sky.mjs -> Editor/SkyOracle.cs) can compare it with the JS bit for bit.
//
// The one part that is not a port of the JS: the sun's transmittance. The JS reads it back from its atmosphere LUT on the GPU; here it is integrated on
// the CPU with the same medium (Atmosphere.js atmosphereMedium: Hillaire 2020, Rayleigh / Mie / ozone, the same coefficients and scale heights).
namespace Tidewater.Sky
{
	public sealed class SkyState
	{
		public Vector3 sun = new Vector3();   // the real sun (atmosphere.sunDir): may be below the horizon
		public Vector3 moon = new Vector3();  // sky.moonDir
		public Vector3 key = new Vector3();   // G.sunDir: the sun until it is well below the horizon, then the moon
		public bool sunIsKey;                 // dir.y > -0.07
		public double night;                  // G.night, also the star intensity
	}

	public static class SkyMath
	{
		public const double SUN_ILLUMINANCE = 11.0;   // Atmosphere.js: scene units (sun irradiance outside the atmosphere)
		public const double SUN_ANGULAR_RADIUS = 0.004675 * 1.15;
		public const double KEY_SWITCH = -0.07;       // the sun is the key light above this height, the moon below it
		const double RG = 6360.0, RT = 6460.0;        // Atmosphere.js: ground and top of the atmosphere (km)
		public const double VIEW_HEIGHT = RG + 0.002; // Atmosphere.js viewHeight default (km)

		static readonly Vector3 UP = new Vector3( 0, 1, 0 );

		// Sky.js sunDirectionFromTime: the sun for a clock hour at 24 deg N with a 6 deg declination (three.js axes: x east, y up, -z north)
		public static Vector3 sunDirectionFromTime( double hours, double latitudeDeg = 24, double declinationDeg = 6, Vector3 @out = null )
		{
			if ( @out == null ) @out = new Vector3();
			double phi = MathUtils.degToRad( latitudeDeg );
			double dec = MathUtils.degToRad( declinationDeg );
			double H = MathUtils.degToRad( ( hours - 12 ) * 15 );
			double east = - Math.Cos( dec ) * Math.Sin( H );
			double north = Math.Cos( phi ) * Math.Sin( dec ) - Math.Sin( phi ) * Math.Cos( dec ) * Math.Cos( H );
			double up = Math.Sin( phi ) * Math.Sin( dec ) + Math.Cos( phi ) * Math.Cos( dec ) * Math.Cos( H );
			return @out.set( east, up, - north ).normalize();
		}

		// App.updateSun: everything the sun and moon decide for a clock hour and the sun's azimuth setting (degrees)
		public static SkyState At( double hours, double sunAzimuth = 0, SkyState @out = null )
		{
			if ( @out == null ) @out = new SkyState();
			var dir = sunDirectionFromTime( hours ).applyAxisAngle( UP, MathUtils.degToRad( sunAzimuth ) );
			@out.sun.copy( dir );
			// below the horizon the moon takes over as the key light
			@out.night = MathUtils.smoothstep( - dir.y, 0.02, 0.18 );
			@out.moon.set( - dir.x, Math.Abs( dir.y ) * 0.8 + 0.25, - dir.z ).normalize();
			@out.sunIsKey = dir.y > KEY_SWITCH;
			@out.key.copy( @out.sunIsKey ? dir : @out.moon );
			return @out;
		}

		// the sun's transmittance to space for a sun at height `sunY` (sin of its elevation) seen from sea level: Atmosphere.js atmosphereMedium
		// integrated along the ray (the JS reads the same quantity from its LUT)
		public static Vector3 sunTransmittance( double sunY, Vector3 @out = null )
		{
			if ( @out == null ) @out = new Vector3();
			double r = VIEW_HEIGHT, mu = MathUtils.clamp( sunY, -1, 1 );
			// distance from the viewer to the top of the atmosphere along the ray
			double disc = r * r * ( mu * mu - 1 ) + RT * RT;
			double d = Math.Max( 0, - r * mu + Math.Sqrt( Math.Max( disc, 0 ) ) );
			const int N = 256;
			double dt = d / N, tr = 0, tg = 0, tb = 0;
			for ( int i = 0; i < N; i ++ )
			{
				double t = ( i + 0.5 ) * dt;
				double h = Math.Sqrt( r * r + t * t + 2 * r * mu * t ) - RG;
				double ray = Math.Exp( - h / 8.0 ), mie = Math.Exp( - h / 1.2 ), ozone = Math.Max( 0, 1 - Math.Abs( h - 25 ) / 15 );
				double m = 4.440e-3 * mie;
				tr += 5.802e-3 * ray + m + 0.650e-3 * ozone;
				tg += 13.558e-3 * ray + m + 1.881e-3 * ozone;
				tb += 33.1e-3 * ray + m + 0.085e-3 * ozone;
			}

			return @out.set( Math.Exp( - tr * dt ), Math.Exp( - tg * dt ), Math.Exp( - tb * dt ) );
		}

		// App.applyAtmosphereReadback: the key light's colour in scene units (the sun's irradiance outside the atmosphere is 11)
		public static Vector3 keyColor( SkyState s, Vector3 @out = null )
		{
			if ( @out == null ) @out = new Vector3();
			if ( s.sunIsKey )
			{
				double horizonFade = MathUtils.smoothstep( s.sun.y, -0.03, 0.02 );
				return sunTransmittance( s.sun.y, @out ).multiplyScalar( SUN_ILLUMINANCE * horizonFade );
			}

			return @out.set( 0.6, 0.7, 1.0 ).multiplyScalar( 0.12 * s.night );
		}

		// the night's share of the ambient light: added to the sky irradiance as ( 0.6, 0.7, 1 ) * this
		public static double nightAmbient( double night ) => 0.012 * night;

		// PostFX.js auto exposure (the eye): the multiplier the meter aims for, given the centre-weighted average scene luminance (scene units) and the night.
		// The eye only partly compensates a dark scene (^0.8), within [0.6, 6]; at night it gains at most one extra stop (the ceiling falls to 2)
		public const double AE_REF_LUM = 0.25, AE_MIN = 0.6, AE_MAX = 6.0;
		public static double exposureTarget( double avg, double night )
		{
			double ratio = AE_REF_LUM / avg;
			double partial = ratio > 1.0 ? Math.Pow( ratio, 0.8 ) : ratio;
			return MathUtils.clamp( partial, AE_MIN, MathUtils.lerp( AE_MAX, 2.0, night ) );
		}

		// Sky.js skyStars `dark`: 1 once the sun is well below the horizon (the stars come out brightest first, the faintest in full dark)
		public static double starDark( double sunY ) => 1.0 - MathUtils.smoothstep( sunY, -0.28, -0.1 );
	}
}
