using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Core;
using Tidewater.Game;
using UnityEngine;

// Compares the C# Bindings (Core/Bindings.cs: rebind, steal on conflict, clear, reset, the options, the stored file and its repair) with the real JS class:
//   node unity/tools/dump-bindings.mjs unity/Temp/oracle/bindings
//   unity/tools/bindings-oracle.sh
// The file is a script of steps run on the JS over a memory store, with after every step the result and a snapshot (the lists of every action, the labels and summaries for both
// layouts, isDefault, ownerOf, the options, rev, and the stored text). Here the same steps run on the C# class and every field is compared; the stored text must match exactly. A
// negative control then changes one expected value and must be caught. The `keys` section checks the key-code table both ways: every Unity key that has a KeyboardEvent.code maps
// back to itself, and every code the default table names resolves to a key.
namespace Tidewater.EditorTools
{
	public static class BindingsOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/bindings" ) );

		sealed class Tally
		{
			public int compared, bad; public readonly List<string> first = new List<string>();
			public void Eq( string at, JToken a, JToken b )
			{
				// (a null string made into a JValue is a String with no value, the JS's null a Null: both are "nothing")
				if ( IsNull( a ) || IsNull( b ) ) { compared ++; if ( ! ( IsNull( a ) && IsNull( b ) ) ) Bad( at, a, b ); return; }
				if ( a is JObject ao && b is JObject bo )
				{
					var names = ao.Properties().Select( p => p.Name ).Union( bo.Properties().Select( p => p.Name ) );
					foreach ( var n in names ) Eq( at + "." + n, ao[ n ], bo[ n ] );
					return;
				}

				if ( a is JArray aa && b is JArray ba )
				{
					if ( aa.Count != ba.Count ) { compared ++; Bad( at + ".length", aa.Count, ba.Count ); return; }
					for ( int i = 0; i < aa.Count; i ++ ) Eq( at + "[" + i + "]", aa[ i ], ba[ i ] );
					return;
				}

				compared ++;
				bool num = ( a.Type == JTokenType.Integer || a.Type == JTokenType.Float ) && ( b.Type == JTokenType.Integer || b.Type == JTokenType.Float );
				bool same = num ? ( double ) a == ( double ) b : a.Type == b.Type && JToken.DeepEquals( a, b );
				if ( ! same ) Bad( at, a, b );
			}

			static bool IsNull( JToken t ) => t == null || t.Type == JTokenType.Null || ( t is JValue v && v.Value == null );
			void Bad( string at, object a, object b ) { bad ++; if ( first.Count < 6 ) first.Add( $"{at}: {a} vs JS {b}" ); }
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ" + ( bad > 0 ? "  <-- MISMATCH" : "" );
		}

		static Device Dev( string s ) => s == "kb" ? Device.kb : s == "mouse" ? Device.mouse : s == "pad" ? Device.pad : ( Device ) ( - 1 );

		static JObject Snapshot( Bindings b, MemorySaveStore store )
		{
			var actions = new JObject();
			foreach ( var a in Bindings.ACTIONS )
			{
				var o = new JObject { [ "isDefault" ] = b.isDefault( a.id ), [ "summaryXbox" ] = b.summary( a.id, "xbox" ), [ "summaryPs" ] = b.summary( a.id, "ps" ) };
				foreach ( var d in Bindings.DEVICES ) o[ d.ToString() ] = new JArray( b.list( a.id, d ).Select( e => new JObject { [ "v" ] = e.v, [ "sign" ] = e.sign } ) );
				foreach ( var d in Bindings.DEVICES ) { o[ "label_" + d ] = b.label( a.id, d, "xbox" ); o[ "labelPs_" + d ] = b.label( a.id, d, "ps" ); }
				actions[ a.id ] = o;
			}

			return new JObject
			{
				[ "actions" ] = actions,
				[ "opts" ] = new JObject { [ "padEnabled" ] = b.opts.padEnabled, [ "deadzone" ] = b.opts.deadzone, [ "lookSensitivity" ] = b.opts.lookSensitivity, [ "invertY" ] = b.opts.invertY, [ "rumble" ] = b.opts.rumble, [ "flashlightOn" ] = b.opts.flashlightOn },
				[ "rev" ] = b.rev,
				[ "owners" ] = new JObject { [ "KeyE" ] = b.ownerOf( Device.kb, "KeyE" ), [ "KeyG" ] = b.ownerOf( Device.kb, "KeyG" ), [ "LMB" ] = b.ownerOf( Device.mouse, "LMB" ), [ "MMB" ] = b.ownerOf( Device.mouse, "MMB" ), [ "A" ] = b.ownerOf( Device.pad, "A" ), [ "RSX" ] = b.ownerOf( Device.pad, "RSX" ), [ "Nope" ] = b.ownerOf( Device.kb, "Nope" ) },
				[ "stored" ] = store.GetItem( Bindings.STORE_KEY ),
			};
		}

		public static string Compare( string dir = null, string only = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "bindings.json" ) ) );
			var sb = new StringBuilder();
			if ( only == null || only == "steps" ) Steps( js, sb );
			if ( only == null || only == "keys" ) Keys( sb );
			return sb.ToString();
		}

		static void Steps( JObject js, StringBuilder sb )
		{
			var steps = ( JArray ) js[ "steps" ];
			var ids = ( ( JArray ) js[ "actionIds" ] ).Select( t => ( string ) t ).ToArray();
			sb.AppendLine( Bindings.ACTIONS.Select( a => a.id ).SequenceEqual( ids ) ? $"  action table: {ids.Length} actions, same ids in the same order" : "  action table: DIFFERENT IDS  <-- MISMATCH" );
			var store = new MemorySaveStore();
			var b = new Bindings( store );
			var all = new Tally(); var control = new Tally();
			var results = new Tally();
			for ( int i = 0; i < steps.Count; i ++ )
			{
				var st = ( JObject ) steps[ i ];
				string op = ( string ) st[ "op" ]; var id = ( string ) st[ "id" ]; var dev = Dev( ( string ) st[ "device" ] );
				JToken result = JValue.CreateNull();
				switch ( op )
				{
					case "init": break;
					case "add":
						result = new JArray( b.add( id, dev, st[ "sign" ] != null ? new Bind( ( string ) st[ "v" ], ( int ) st[ "sign" ] ) : new Bind( ( string ) st[ "v" ] ) ) ); break;
					case "remove": result = b.remove( id, dev, ( string ) st[ "v" ] ); break;
					case "clear": result = b.clear( id ); break;
					case "reset": result = b.reset( id ); break;
					case "resetAll": b.resetAll(); break;
					case "set": result = ( int ) dev >= 0 && b.set( id, dev, ( ( JArray ) st[ "entries" ] ).Select( Bindings.EntryOf ) ); break;
					case "opt":
						{
							string k = ( string ) st[ "key" ]; var v = st[ "value" ];
							switch ( k )
							{
								case "deadzone": b.opts.deadzone = ( double ) v; break;
								case "padEnabled": b.opts.padEnabled = ( bool ) v; break;
								case "invertY": b.opts.invertY = ( bool ) v; break;
								case "lookSensitivity": b.opts.lookSensitivity = ( double ) v; break;
								case "rumble": b.opts.rumble = ( double ) v; break;
								case "flashlightOn": b.opts.flashlightOn = ( bool ) v; break;
								default: throw new Exception( "opt " + k );
							}
							break;
						}
					case "save": b.save(); break;
					case "reload": b = new Bindings( store ); break;
					case "raw": store.items[ Bindings.STORE_KEY ] = ( string ) st[ "value" ]; break;
					case "rawNone": store.items.Remove( Bindings.STORE_KEY ); break;
					case "load": result = b.load(); break;
					default: throw new Exception( "op " + op );
				}

				results.Eq( $"step {i} {op} result", result, st[ "result" ] );
				var snap = Snapshot( b, store );
				all.Eq( $"step {i} {op}", snap, st[ "snap" ] );
				// the negative control: the same comparison with one expected value changed must report it
				if ( i == 2 )
				{
					var wrong = ( JObject ) st[ "snap" ].DeepClone();
					wrong[ "actions" ][ "interact" ][ "kb" ][ 0 ][ "v" ] = "KeyZ";
					control.Eq( "control", snap, wrong );
				}
			}

			sb.AppendLine( results.Line( $"results of {steps.Count} steps" ) );
			foreach ( var f in results.first ) sb.AppendLine( "    " + f );
			sb.AppendLine( all.Line( $"snapshots of {steps.Count} steps (lists, labels, summaries, owners, options, rev, stored text)" ) );
			foreach ( var f in all.first ) sb.AppendLine( "    " + f );
			sb.AppendLine( control.bad > 0 ? $"  negative control: a changed expected value is caught ({control.bad} difference)" : "  negative control: NOT CAUGHT  <-- MISMATCH" );
		}

		static void Keys( StringBuilder sb )
		{
			var r = GameInput.CheckKeyTable();
			sb.AppendLine( $"  key codes: {r.coded} of {r.keys} Unity keys have a KeyboardEvent.code, {r.bad} do not map back to the same key" + ( r.bad > 0 ? "  <-- MISMATCH  " + r.detail : "" ) );
			sb.AppendLine( $"  key codes: {r.unresolved} codes of the default table do not resolve" + ( r.unresolved > 0 ? "  <-- MISMATCH  " + r.detail : "" ) + $"; spot checks {( r.spotFail.Length == 0 ? "all resolve" : "FAIL: " + r.spotFail )}" );
		}
	}
}
