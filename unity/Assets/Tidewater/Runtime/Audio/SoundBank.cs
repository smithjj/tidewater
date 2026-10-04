using System.Collections.Generic;

// Port of src/audio/soundBank.js: the recorded sounds (Resources/audio, sources and licences in CREDITS.md next to them; all CC0). Loops are
// crossfaded at the wrap and loudness-normalised to about -23 LUFS. One-shot sprites hold peak-normalised slices [start s, duration s].
// `lufs`: measured loudness at unity gain (loops: median momentary loudness; sprites: each slice's maximum momentary loudness) - the mixer
// turns its target levels into gains with it. The browser serves them as Opus in Ogg; Unity's importer reads only Vorbis, so the files here
// are the same recordings decoded and re-encoded as Vorbis (same length, level within 0.4 dB).
namespace Tidewater.Audio
{
	public sealed class Clip
	{
		public readonly string file;       // Resources path under audio/
		public readonly bool loop;
		public readonly double lufs;       // loops
		public readonly double[] lufsAt;   // sprites: per slice
		public readonly double[,] slices;  // sprites: [start s, duration s] per slice

		public Clip( string file, double lufs ) { this.file = file; loop = true; this.lufs = lufs; }
		public Clip( string file, double[] lufsAt, double[,] slices ) { this.file = file; this.lufsAt = lufsAt; this.slices = slices; }
		public int sliceCount => slices.GetLength( 0 );
	}

	public static class SoundBank
	{
		public static readonly Dictionary<string, Clip> BANK = new Dictionary<string, Clip>
		{
			{ "surf_far", new Clip( "surf_far", -23.0 ) },
			{ "wind", new Clip( "wind", -23.6 ) },
			{ "palms", new Clip( "palms", -23.3 ) },
			{ "crickets", new Clip( "crickets", -23.0 ) },
			{ "pier_lap", new Clip( "pier_lap", -30.7 ) },
			{ "under_reef", new Clip( "under_reef", -23.0 ) },
			{ "boat_engine", new Clip( "boat_engine", -23.0 ) },
			{ "boat_lap", new Clip( "boat_lap", -32.2 ) },
			{ "boat_rush", new Clip( "boat_rush", -23.4 ) },
			{ "birds_dawn", new Clip( "birds_dawn", -23.3 ) },
			{ "whale_song", new Clip( "whale_song", -25.5 ) },
			{ "step_sand", new Clip( "step_sand", new[] { -25.0, -25.4, -27.0, -22.5, -19.8 }, new[,] { { 0.08, 0.191 }, { 0.351, 0.3153 }, { 0.7463, 0.2237 }, { 1.05, 0.516 }, { 1.646, 0.436 } } ) },
			{ "step_wetsand", new Clip( "step_wetsand", new[] { -27.4, -19.6, -21.7, -20.9, -20.1 }, new[,] { { 0.08, 0.171 }, { 0.331, 0.376 }, { 0.787, 0.226 }, { 1.093, 0.246 }, { 1.419, 0.221 } } ) },
			{ "surf_crash", new Clip( "surf_crash", new[] { -13.0, -13.4, -14.1, -17.0, -13.0, -14.0, -12.7 }, new[,] { { 0.08, 3.254 }, { 3.414, 2.504 }, { 5.998, 2.554 }, { 8.632, 1.304 }, { 10.016, 2.804 }, { 12.9, 2.204 }, { 15.184, 2.7493 } } ) },
			{ "surf_wash", new Clip( "surf_wash", new[] { -12.4, -11.3, -16.5, -13.7, -14.5, -22.3 }, new[,] { { 0.08, 1.404 }, { 1.564, 2.054 }, { 3.698, 1.504 }, { 5.282, 1.354 }, { 6.716, 1.904 }, { 8.7, 1.154 } } ) },
			{ "surf_backwash", new Clip( "surf_backwash", new[] { -13.1, -17.7, -22.1, -16.6, -14.4, -15.9 }, new[,] { { 0.08, 1.804 }, { 1.964, 1.904 }, { 3.948, 3.804 }, { 7.832, 2.254 }, { 10.166, 2.394 }, { 12.64, 2.404 } } ) },
			{ "step_wood", new Clip( "step_wood", new[] { -22.5, -20.9, -20.8, -20.2, -20.8 }, new[,] { { 0.08, 0.4267 }, { 0.5867, 0.424 }, { 1.0907, 0.424 }, { 1.5947, 0.417 }, { 2.0917, 0.4326 } } ) },
			{ "step_water", new Clip( "step_water", new[] { -17.5, -16.0, -19.5, -23.0, -14.9 }, new[,] { { 0.08, 0.854 }, { 1.014, 0.704 }, { 1.798, 0.624 }, { 2.502, 0.624 }, { 3.206, 0.854 } } ) },
			{ "step_grass", new Clip( "step_grass", new[] { -20.7, -18.0, -19.3, -19.5, -18.6 }, new[,] { { 0.08, 0.304 }, { 0.464, 0.3553 }, { 0.8993, 0.284 }, { 1.2633, 0.254 }, { 1.5973, 0.2473 } } ) },
			{ "step_rock", new Clip( "step_rock", new[] { -27.1, -24.8, -24.7, -24.8, -25.6 }, new[,] { { 0.08, 0.504 }, { 0.664, 0.504 }, { 1.248, 0.504 }, { 1.832, 0.504 }, { 2.416, 0.504 } } ) },
			{ "swim", new Clip( "swim", new[] { -18.5, -21.2, -15.6, -23.2 }, new[,] { { 0.08, 0.754 }, { 0.914, 0.5547 }, { 1.5487, 0.604 }, { 2.2327, 0.804 } } ) },
			{ "uw_swim", new Clip( "uw_swim", new[] { -16.0, -23.9, -25.6, -18.6 }, new[,] { { 0.08, 0.626 }, { 0.786, 0.836 }, { 1.702, 0.786 }, { 2.568, 1.1053 } } ) },
			{ "submerge", new Clip( "submerge", new[] { -24.7, -26.6 }, new[,] { { 0.08, 0.804 }, { 0.964, 0.754 } } ) },
			{ "emerge", new Clip( "emerge", new[] { -18.8 }, new[,] { { 0.08, 0.904 } } ) },
			{ "splash", new Clip( "splash", new[] { -19.0, -18.0, -14.1, -15.5 }, new[,] { { 0.08, 1.604 }, { 1.764, 2.2067 }, { 4.0507, 2.2133 }, { 6.344, 1.906 } } ) },
			{ "hull_slap", new Clip( "hull_slap", new[] { -20.7, -18.4, -20.2, -17.7, -16.4 }, new[,] { { 0.08, 0.554 }, { 0.714, 0.754 }, { 1.548, 0.404 }, { 2.032, 0.5087 }, { 2.6207, 0.406 } } ) },
			{ "gull", new Clip( "gull", new[] { -15.9, -16.2, -18.1, -11.8, -12.8, -12.8 }, new[,] { { 0.08, 1.001 }, { 1.161, 1.404 }, { 2.645, 0.904 }, { 3.629, 1.114 }, { 4.823, 1.044 }, { 5.947, 0.5267 } } ) },
			{ "bird_forest", new Clip( "bird_forest", new[] { -12.8, -12.0, -10.9, -11.8, -12.0, -11.3, -11.9, -13.4, -13.7, -11.9, -7.7, -12.1, -11.0 }, new[,] { { 0.08, 1.47 }, { 1.63, 1.45 }, { 3.16, 1.23 }, { 4.47, 1.59 }, { 6.14, 1.55 }, { 7.77, 1.38 }, { 9.23, 1.16 }, { 10.47, 1.3 }, { 11.85, 1.12 }, { 13.05, 1.45 }, { 14.58, 1.99 }, { 16.65, 1.2 }, { 17.93, 1.06 } } ) },
			{ "bird_dove", new Clip( "bird_dove", new[] { -7.7, -7.3, -7.2, -7.4, -7.6 }, new[,] { { 0.08, 3.12 }, { 3.28, 3.18 }, { 6.54, 3.13 }, { 9.75, 3.36 }, { 13.19, 3.31 } } ) },
			{ "tern", new Clip( "tern", new[] { -9.8, -10.7, -12.4, -12.0, -9.0, -12.9 }, new[,] { { 0.08, 2.1097 }, { 2.2697, 1.85 }, { 4.1997, 2.4098 }, { 6.6894, 1.3657 }, { 8.1351, 1.02 }, { 9.2351, 1.99 } } ) },
			{ "whale_blow", new Clip( "whale_blow", new[] { -13.3, -14.2, -14.4 }, new[,] { { 0.08, 2.0057 }, { 2.1657, 1.3331 }, { 3.5789, 1.5137 } } ) },
			{ "big_splash", new Clip( "big_splash", new[] { -15.6, -18.7 }, new[,] { { 0.08, 2.1477 }, { 2.3077, 1.7777 } } ) },
			// fishing (tools/audio/build-fishing.mjs)
			{ "reel_wind", new Clip( "reel_wind", -23.0 ) },
			{ "reel_drag", new Clip( "reel_drag", -22.6 ) },
			{ "line_strain", new Clip( "line_strain", -24.4 ) },
			{ "rod_swish", new Clip( "rod_swish", new[] { -18.3, -22.1, -20.9, -18.4, -16.0 }, new[,] { { 0.08, 0.404 }, { 0.564, 0.4453 }, { 1.0893, 0.604 }, { 1.7733, 0.424 }, { 2.2773, 0.604 } } ) },
			{ "bail_click", new Clip( "bail_click", new[] { -30.0, -29.8, -31.1, -29.3 }, new[,] { { 0.08, 0.164 }, { 0.324, 0.174 }, { 0.578, 0.144 }, { 0.802, 0.184 } } ) },
			{ "line_out", new Clip( "line_out", new[] { -15.9 }, new[,] { { 0.08, 0.644 } } ) },
			{ "plop", new Clip( "plop", new[] { -21.4, -20.7 }, new[,] { { 0.08, 0.624 }, { 0.784, 0.264 } } ) },
			{ "line_snap", new Clip( "line_snap", new[] { -17.9, -23.4, -20.8, -23.2 }, new[,] { { 0.08, 0.554 }, { 0.714, 0.504 }, { 1.298, 0.504 }, { 1.882, 0.504 } } ) },
			{ "fish_splash", new Clip( "fish_splash", new[] { -16.0, -14.2, -16.6, -17.0, -15.8 }, new[,] { { 0.08, 0.464 }, { 0.624, 0.324 }, { 1.028, 0.504 }, { 1.612, 1.204 }, { 2.896, 1.304 } } ) },
			{ "fish_flop", new Clip( "fish_flop", new[] { -24.6, -23.9, -23.0, -17.0 }, new[,] { { 0.08, 1.354 }, { 1.514, 0.304 }, { 1.898, 0.304 }, { 2.282, 0.324 } } ) },
			{ "coins", new Clip( "coins", new[] { -16.5 }, new[,] { { 0.08, 1.604 } } ) },
		};
	}
}
