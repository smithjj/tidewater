using UnityEditor;
using UnityEngine;

// Drives the settings panel in a Play session: the Editor has no pointer of its own to move, so this sends real IMGUI events to the Game view window, in the game's own
// coordinates (0,0 = the top-left of the game, y down). Used as: tools/ev.sh 'return Tidewater.EditorTools.SettingsDebug.Click( 740, 91 );'
// (A pointer event has to go through the window: IMGUI only maps the mouse into a group or a scroll view for a real event, not for a Repaint rewritten to look like one.)
namespace Tidewater.EditorTools
{
	public static class SettingsDebug
	{
		static EditorWindow gameView;

		public static string Send( EventType type, float x, float y, int clicks = 1, KeyCode key = KeyCode.None, char ch = '\0', bool shift = false )
		{
			if ( gameView == null )
			{
				var all = Resources.FindObjectsOfTypeAll( typeof( Editor ).Assembly.GetType( "UnityEditor.GameView" ) );
				if ( all.Length == 0 ) return "no game view";
				gameView = all[ 0 ] as EditorWindow;
			}

			float top = 21; // (the Game view's own toolbar)
			gameView.SendEvent( new Event { type = type, mousePosition = new Vector2( x, y + top ), clickCount = clicks, keyCode = key, character = ch, modifiers = shift ? EventModifiers.Shift : EventModifiers.None } );
			EditorApplication.QueuePlayerLoopUpdate();
			gameView.Repaint();
			return type + " " + x + "," + y;
		}

		public static string Move( float x, float y ) => Send( EventType.MouseMove, x, y );
		public static string Down( float x, float y, int clicks = 1 ) => Send( EventType.MouseDown, x, y, clicks );
		public static string Up( float x, float y ) => Send( EventType.MouseUp, x, y );
		public static string Drag( float x, float y, bool shift = false ) => Send( EventType.MouseDrag, x, y, 1, KeyCode.None, '\0', shift );
		public static string Click( float x, float y, int clicks = 1 ) { Move( x, y ); Down( x, y, clicks ); return Up( x, y ); }
		public static string Wheel( float x, float y, float dy ) { Move( x, y ); gameView.SendEvent( new Event { type = EventType.ScrollWheel, mousePosition = new Vector2( x, y + 21 ), delta = new Vector2( 0, dy ) } ); return "wheel"; }
		public static string Key( KeyCode k, char ch = '\0' ) { Send( EventType.KeyDown, 0, 0, 1, k, ch ); return Send( EventType.KeyUp, 0, 0, 1, k, ch ); }
		public static string Type( string s ) { foreach ( char c in s ) Send( EventType.KeyDown, 0, 0, 1, KeyCode.None, c ); return "typed " + s; }
	}
}
