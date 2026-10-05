using System.Collections.Generic;
using UnityEngine;

// The web UI's icons (src/ui/icons.js) for the IMGUI panels: the JS draws each as an inline stroke SVG in the current colour, so here every one is a white-on-transparent
// 96 px PNG (Resources/ui/icons/<name>.png, rasterized from the JS paths by tools/make-ui-icons.py) drawn tinted. The JS aliases (ocean -> wave, shore -> palm ...) are the
// table below; a name that is neither an alias nor a file is the dot, as in the JS.
namespace Tidewater.Game
{
	public static class IconKit
	{
		static readonly Dictionary<string, string> ALIASES = new Dictionary<string, string>
		{
			{ "ocean", "wave" }, { "waves", "wave" }, { "water", "wave" }, { "sea", "wave" },
			{ "shore", "palm" }, { "beach", "palm" }, { "island", "palm" }, { "coast", "palm" },
			{ "sky", "sun" }, { "day", "sun" }, { "light", "sun" }, { "lighting", "sun" }, { "atmosphere", "sun" },
			{ "effects", "sparkles" }, { "fx", "sparkles" }, { "sparkle", "sparkles" }, { "post", "sparkles" }, { "bloom", "sparkles" }, { "quality", "sparkles" },
			{ "performance", "gauge" }, { "perf", "gauge" }, { "speed", "gauge" }, { "fps", "gauge" }, { "stats", "gauge" },
			{ "photo", "viewfinder" }, { "photo-mode", "viewfinder" }, { "lens", "viewfinder" }, { "aperture", "viewfinder" },
			{ "settings", "sliders" }, { "tune", "sliders" }, { "controls", "sliders" },
			{ "time", "clock" }, { "time-of-day", "clock" },
			{ "refresh", "reset" }, { "undo", "reset" }, { "rotate-ccw", "reset" },
			{ "sound", "volume" }, { "audio", "volume" }, { "music", "volume" },
			{ "terrain", "mountain" }, { "gpu", "cpu" }, { "display", "monitor" }, { "screen", "monitor" }, { "render", "monitor" },
			{ "question", "help" }, { "x", "close" }, { "cam", "camera" }, { "color", "palette" }, { "colors", "palette" }, { "grade", "palette" },
			{ "walking", "walk" }, { "swimming", "swim" }, { "diving", "dive" }, { "underwater", "dive" },
			{ "free", "move" }, { "free-camera", "move" }, { "fly", "move" }, { "drone", "move" },
			{ "bubbles", "foam" }, { "spray", "foam" }, { "clouds", "cloud" }, { "weather", "cloud" }, { "rain", "storm" },
			{ "night", "moon" }, { "dusk", "sunset" }, { "dawn", "sunset" }, { "sunrise", "sunset" },
			{ "heading", "compass" }, { "navigation", "compass" }, { "harbor", "anchor" }, { "dock", "anchor" }, { "pier", "anchor" },
			{ "haze", "fog" }, { "mist", "fog" }, { "shadows", "shadow" }, { "lightning", "bolt" }, { "energy", "bolt" },
		};

		static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

		public static Texture2D Get( string name )
		{
			if ( string.IsNullOrEmpty( name ) ) name = "dot";
			if ( cache.TryGetValue( name, out var t ) && t != null ) return t;
			string file = ALIASES.TryGetValue( name, out var a ) ? a : name;
			t = Resources.Load<Texture2D>( "ui/icons/" + file );
			if ( t == null && file != "dot" ) t = Resources.Load<Texture2D>( "ui/icons/dot" );
			cache[ name ] = t;
			return t;
		}

		// the icon in `r` (fitted, square), tinted
		public static void Draw( Rect r, string name, Color tint )
		{
			if ( tint.a <= 0.001f ) return;
			var t = Get( name );
			if ( t == null ) return;
			GUI.DrawTexture( r, t, ScaleMode.ScaleToFit, true, 0, tint, 0, 0 );
		}

		// a square icon of `size` centred on (cx, cy)
		public static void DrawAt( float cx, float cy, float size, string name, Color tint ) => Draw( new Rect( cx - size / 2, cy - size / 2, size, size ), name, tint );
	}
}
