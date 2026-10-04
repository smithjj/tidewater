using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# fish port with the JS original (unity/tools/dump-fish.mjs -> Temp/oracle/fish): the models of FishGeometry / FishProps
// (every species at every level of detail and pose, the cut and split fish, ice, leaf, lobster: positions, normals, aData, indices)
// and the FishProps items the village adds (placement, orientation, pattern, bends, flags).
namespace Tidewater.EditorTools
{
	public static class FishOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/fish" ) );

		[MenuItem( "Tidewater/Compare fish with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare( DefaultDir ) );

		static double MaxDiff( float[] got, float[] want, out int at )
		{
			double max = 0; at = -1;
			for ( int i = 0; i < want.Length; i ++ )
			{
				double d = Math.Abs( ( double ) got[ i ] - want[ i ] );
				if ( d > max ) { max = d; at = i; }
			}

			return max;
		}

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var cases = JArray.Parse( File.ReadAllText( dir + "/models.json" ) );
			var bin = File.ReadAllBytes( dir + "/models.bin" );
			int bad = 0, worstCase = -1; double worstPos = 0, worstNrm = 0, worstDat = 0; int idxBad = 0, countBad = 0;
			foreach ( var c in cases )
			{
				string name = ( string ) c[ "name" ]; int nv = ( int ) c[ "vertices" ], ni = ( int ) c[ "indices" ]; long off = ( long ) c[ "offset" ];
				var p = name.Split( ':' );
				BufferGeometry g;
				if ( p[ 0 ] == "whole" )
				{
					int lod = int.Parse( p[ 2 ] ); bool dead = p[ 3 ] == "dead";
					g = FishGeometry.fishGeometry( FishSpecies.SPECIES[ p[ 1 ] ], new FishGeoOpts { lod = lod, pose = dead ? "dead" : "swim", eyes = dead ? ( bool? ) ( lod == 0 ) : null } );
				}
				else if ( p[ 0 ] == "creature" ) g = p[ 1 ] == "turtle" ? CreatureGeometry.turtleGeometry( int.Parse( p[ 2 ] ) ) : p[ 1 ] == "eagleModel" ? CreatureGeometry.eagleRayGeometry( int.Parse( p[ 2 ] ) ) : CreatureGeometry.rayGeometry( int.Parse( p[ 2 ] ), p[ 1 ] == "eagleRay" );
					else g = FishProps.geometryOf( p[ 0 ], p[ 1 ].Length > 0 ? p[ 1 ] : null, int.Parse( p[ 2 ] ) );

				var pos = g.getAttribute( "position" ).array; var nrm = g.getAttribute( "normal" ).array; var dat = g.getAttribute( "aData" ).array;
				int gi = g.index.array.Length;
				if ( pos.Length / 3 != nv || gi != ni ) { countBad ++; sb.AppendLine( $"{name}: COUNT {pos.Length / 3} / {gi} (want {nv} / {ni})" ); continue; }
				float[] rd( long o, int n ) { var a = new float[ n ]; Buffer.BlockCopy( bin, ( int ) o, a, 0, n * 4 ); return a; }
				var wp = rd( off, nv * 3 ); var wn = rd( off + nv * 12, nv * 3 ); var wd = rd( off + nv * 24, nv * 4 );
				var wi = new uint[ ni ]; Buffer.BlockCopy( bin, ( int ) ( off + nv * 40 ), wi, 0, ni * 4 );
				double dp = MaxDiff( pos, wp, out int ap ), dn = MaxDiff( nrm, wn, out int an ), dd = MaxDiff( dat, wd, out int ad );
				int ib = 0; for ( int i = 0; i < ni; i ++ ) if ( wi[ i ] != ( uint ) g.index.array[ i ] ) ib ++;
				idxBad += ib;
				if ( dp > worstPos ) { worstPos = dp; worstCase = bad; }
				worstNrm = Math.Max( worstNrm, dn ); worstDat = Math.Max( worstDat, dd );
				if ( dp > 1e-5 || dn > 1e-3 || dd > 1e-5 || ib > 0 ) { bad ++; sb.AppendLine( $"{name}: pos {dp:E2} (at {ap}) nrm {dn:E2} (at {an}) aData {dd:E2} (at {ad}) idx mismatches {ib}" ); }
			}

			sb.AppendLine( $"models: {cases.Count} compared, {bad} outside tolerance, {countBad} with a different size, index mismatches {idxBad}; worst: pos {worstPos:E2} normal {worstNrm:E2} aData {worstDat:E2}" );

			// the items of the village
			var J = JObject.Parse( File.ReadAllText( dir + "/props.json" ) );
			var T = new TerrainData( ( int ) J[ "seed" ] );
			var v = new Tidewater.World.Village.Village( T, new Colliders() );
			var items = ( JArray ) J[ "items" ];
			var mine = v.fishProps.items;
			sb.AppendLine( $"fish items: {mine.Count} (want {items.Count})" );
			if ( mine.Count == items.Count )
			{
				double wP = 0, wQ = 0, wF = 0, wS = 0; int kindBad = 0, seedBad = 0;
				for ( int i = 0; i < items.Count; i ++ )
				{
					var a = mine[ i ]; var b = items[ i ];
					if ( a.kind != ( string ) b[ "kind" ] || ( a.species ?? "" ) != ( ( string ) b[ "species" ] ?? "" ) ) { kindBad ++; sb.AppendLine( $"  item {i}: {a.kind}/{a.species} (want {b[ "kind" ]}/{b[ "species" ]})" ); continue; }
					wP = Math.Max( wP, Math.Max( Math.Abs( a.x - ( double ) b[ "x" ] ), Math.Max( Math.Abs( a.y - ( double ) b[ "y" ] ), Math.Max( Math.Abs( a.z - ( double ) b[ "z" ] ), Math.Abs( a.L - ( double ) b[ "L" ] ) ) ) ) );
					for ( int k = 0; k < 4; k ++ ) wQ = Math.Max( wQ, Math.Abs( a.q[ k ] - ( double ) b[ "q" ][ k ] ) );
					for ( int k = 0; k < 4; k ++ ) wF = Math.Max( wF, Math.Abs( a.flags[ k ] - ( double ) b[ "flags" ][ k ] ) );
					wF = Math.Max( wF, Math.Max( Math.Abs( a.curl - ( double ) b[ "curl" ] ), Math.Max( Math.Abs( a.sag - ( double ) b[ "sag" ] ), Math.Max( Math.Abs( a.jaw - ( double ) b[ "jaw" ] ), Math.Abs( a.pattern - ( double ) b[ "pattern" ] ) ) ) ) );
					double ds = Math.Abs( a.seed - ( double ) b[ "seed" ] );
					if ( ds > 1e-9 ) seedBad ++; else wS = Math.Max( wS, ds );
				}

				sb.AppendLine( $"  placement max diff {wP:E2}, orientation {wQ:E2}, bends / flags / pattern {wF:E2}; kind / species mismatches {kindBad}; seeds differing {seedBad} (random ones: the JS draws Math.random)" );
			}

			return sb.ToString();
		}
	}
}
