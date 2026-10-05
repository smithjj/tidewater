using UnityEngine;

// The web UI's fonts (index.html, src/ui/ui.css), as the IMGUI fonts: Inter for text, JetBrains Mono for numbers and keys, Caveat Brush for the big names and
// Kalam for the handwritten lines. The browser picks a weight per face; a Unity dynamic font has one weight per file, so each weight is its own Font and the
// styles are given the face they want (never FontStyle.Bold, which would be a synthesized bold of the regular face). The files are SIL OFL 1.1
// (Resources/fonts/OFL-*.txt, CREDITS.md). Sizes stay as the styles set them: the browser's pixel sizes are in the card's own units.
namespace Tidewater.Game
{
	public static class UIFonts
	{
		static Font Load( string name ) => Resources.Load<Font>( "fonts/" + name );

		static Font inter, interMedium, interSemi, interBold, mono, monoMedium, display, hand, handBold;

		public static Font Inter => inter != null ? inter : inter = Load( "Inter-400" );
		public static Font InterMedium => interMedium != null ? interMedium : interMedium = Load( "Inter-500" );
		public static Font InterSemi => interSemi != null ? interSemi : interSemi = Load( "Inter-600" );
		public static Font InterBold => interBold != null ? interBold : interBold = Load( "Inter-700" );
		public static Font Mono => mono != null ? mono : mono = Load( "JetBrainsMono-400" );
		public static Font MonoMedium => monoMedium != null ? monoMedium : monoMedium = Load( "JetBrainsMono-500" );
		public static Font Display => display != null ? display : display = Load( "CaveatBrush-400" ); // Caveat Brush: one weight
		public static Font Hand => hand != null ? hand : hand = Load( "Kalam-400" );
		public static Font HandBold => handBold != null ? handBold : handBold = Load( "Kalam-700" );

		// a label style in `font` (a null font leaves the skin's)
		public static GUIStyle Label( Font font, int size, TextAnchor align = TextAnchor.UpperLeft, FontStyle fs = FontStyle.Normal )
		{
			var s = new GUIStyle( GUI.skin.label ) { font = font, fontSize = size, alignment = align, fontStyle = fs };
			return s;
		}
	}
}
