using System;

// The CPU half of src/world/reef/ReefBatch.js for the swimming fish: every frame the owner decides which instances are drawn with which
// kind (a model at one level of detail) and calls commit(): the instance ids of each kind are packed into one list (a counting sort, so
// the order within a kind is the order of the adds). Instances in a level-of-detail transition band go to a second channel, the fade list,
// with their fade share (7 bits) and whether this draw is the outgoing level. FishSchoolsView turns the lists into GPU buffers and draws.
namespace Tidewater.World.Fish
{
	public sealed class FishBatch
	{
		public readonly int kinds, maxInstances;
		public readonly float[] data;      // 16 floats per instance (the record of FishMaterial.js swimVertex)
		public readonly uint[] list, fadeList;
		public readonly int[] baseArray, fadeBaseArray, counts, fadeCounts;
		readonly int[] cursor, fadeCursor;
		readonly ushort[] pairKind, fadeKind;
		readonly uint[] pairId, fadeVal;
		int pairs, fadePairs;
		public int visibleInstances, fadeInstances;

		public FishBatch( int kinds, int maxInstances )
		{
			this.kinds = kinds;
			this.maxInstances = maxInstances;
			data = new float[ maxInstances * 16 ];
			int cap = maxInstances * 2;
			list = new uint[ cap ]; fadeList = new uint[ cap ];
			baseArray = new int[ kinds ]; fadeBaseArray = new int[ kinds ];
			counts = new int[ kinds ]; fadeCounts = new int[ kinds ];
			cursor = new int[ kinds ]; fadeCursor = new int[ kinds ];
			pairKind = new ushort[ cap ]; fadeKind = new ushort[ cap ];
			pairId = new uint[ cap ]; fadeVal = new uint[ cap ];
		}

		public void begin()
		{
			pairs = 0; fadePairs = 0;
			Array.Clear( counts, 0, counts.Length );
			Array.Clear( fadeCounts, 0, fadeCounts.Length );
		}

		public void add( int kind, int id )
		{
			int n = pairs ++;
			pairKind[ n ] = ( ushort ) kind;
			pairId[ n ] = ( uint ) id;
			counts[ kind ] ++;
		}

		// fade channel entry: fade = share of this level that is visible (LODFade.js)
		public void addFade( int kind, int id, double fade, bool outgoing )
		{
			int n = fadePairs ++;
			fadeKind[ n ] = ( ushort ) kind;
			int q = ( int ) Tidewater.Engine.JS.Round( Math.Min( 1, Math.Max( 0, fade ) ) * 127 );
			fadeVal[ n ] = unchecked( ( uint ) ( ( id & 0xffffff ) | ( q << 24 ) | ( outgoing ? ( int ) 0x80000000 : 0 ) ) );
			fadeCounts[ kind ] ++;
		}

		public void commit()
		{
			int n = 0;
			for ( int k = 0; k < kinds; k ++ ) { baseArray[ k ] = n; cursor[ k ] = n; n += counts[ k ]; }
			for ( int i = 0; i < pairs; i ++ ) list[ cursor[ pairKind[ i ] ] ++ ] = pairId[ i ];
			visibleInstances = n;
			int m = 0;
			for ( int k = 0; k < kinds; k ++ ) { fadeBaseArray[ k ] = m; fadeCursor[ k ] = m; m += fadeCounts[ k ]; }
			for ( int i = 0; i < fadePairs; i ++ ) fadeList[ fadeCursor[ fadeKind[ i ] ] ++ ] = fadeVal[ i ];
			fadeInstances = m;
		}
	}
}
