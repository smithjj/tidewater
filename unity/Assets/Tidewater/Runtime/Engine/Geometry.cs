using System;
using System.Collections.Generic;

// Port of src/engine/geometry (BufferAttribute, BufferGeometry, BufferGeometryUtils.mergeGeometries) and Box3 / Sphere as far as the
// procedural content needs them. Attributes are Float32 (like the JS Float32Array: values written are rounded to float), the index is
// an int array, and attribute order is the insertion order.
namespace Tidewater.Engine
{
	public sealed class BufferAttribute
	{
		public float[] array;
		public int itemSize;
		public BufferAttribute( float[] array, int itemSize ) { this.array = array; this.itemSize = itemSize; }
		public BufferAttribute( IList<double> values, int itemSize )
		{
			array = new float[ values.Count ];
			for ( int i = 0; i < array.Length; i ++ ) array[ i ] = ( float ) values[ i ];
			this.itemSize = itemSize;
		}

		public int count => array.Length / itemSize;
		public double getX( int i ) => array[ i * itemSize ];
		public double getY( int i ) => array[ i * itemSize + 1 ];
		public double getZ( int i ) => array[ i * itemSize + 2 ];
		public double getW( int i ) => array[ i * itemSize + 3 ];
		public BufferAttribute setX( int i, double v ) { array[ i * itemSize ] = ( float ) v; return this; }
		public BufferAttribute setY( int i, double v ) { array[ i * itemSize + 1 ] = ( float ) v; return this; }
		public BufferAttribute setZ( int i, double v ) { array[ i * itemSize + 2 ] = ( float ) v; return this; }
		public BufferAttribute setXY( int i, double x, double y ) { array[ i * itemSize ] = ( float ) x; array[ i * itemSize + 1 ] = ( float ) y; return this; }
		public BufferAttribute setXYZ( int i, double x, double y, double z ) { int o = i * itemSize; array[ o ] = ( float ) x; array[ o + 1 ] = ( float ) y; array[ o + 2 ] = ( float ) z; return this; }
		public BufferAttribute setXYZW( int i, double x, double y, double z, double w ) { int o = i * itemSize; array[ o ] = ( float ) x; array[ o + 1 ] = ( float ) y; array[ o + 2 ] = ( float ) z; array[ o + 3 ] = ( float ) w; return this; }

		public BufferAttribute applyMatrix4( Matrix4 m )
		{
			var v = new Vector3();
			for ( int i = 0; i < count; i ++ ) { v.fromBufferAttribute( this, i ).applyMatrix4( m ); setXYZ( i, v.x, v.y, v.z ); }
			return this;
		}

		public BufferAttribute applyNormalMatrix( Matrix3 m )
		{
			var v = new Vector3();
			for ( int i = 0; i < count; i ++ ) { v.fromBufferAttribute( this, i ).applyNormalMatrix( m ); setXYZ( i, v.x, v.y, v.z ); }
			return this;
		}

		public BufferAttribute transformDirection( Matrix4 m )
		{
			var v = new Vector3();
			for ( int i = 0; i < count; i ++ ) { v.fromBufferAttribute( this, i ).transformDirection( m ); setXYZ( i, v.x, v.y, v.z ); }
			return this;
		}

		public BufferAttribute clone() => new BufferAttribute( ( float[] ) array.Clone(), itemSize );
	}

	public static class VectorBufferExt
	{
		public static Vector3 fromBufferAttribute( this Vector3 v, BufferAttribute a, int i ) { v.x = a.getX( i ); v.y = a.getY( i ); v.z = a.getZ( i ); return v; }
		public static Vector2 fromBufferAttribute( this Vector2 v, BufferAttribute a, int i ) { v.x = a.getX( i ); v.y = a.getY( i ); return v; }
		public static Vector3 fromArray( this Vector3 v, float[] a, int o = 0 ) { v.x = a[ o ]; v.y = a[ o + 1 ]; v.z = a[ o + 2 ]; return v; }
	}

	public sealed class IndexBuffer
	{
		public int[] array;
		public IndexBuffer( int[] array ) { this.array = array; }
		public int count => array.Length;
		public int getX( int i ) => array[ i ];
		public IndexBuffer clone() => new IndexBuffer( ( int[] ) array.Clone() );
	}

	public sealed class Box3
	{
		public readonly Vector3 min = new Vector3( double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity );
		public readonly Vector3 max = new Vector3( double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity );
		public Box3 makeEmpty() { min.set( double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity ); max.set( double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity ); return this; }
		public Box3 expandByPoint( Vector3 p ) { min.min( p ); max.max( p ); return this; }
		public Box3 setFromBufferAttribute( BufferAttribute a ) { makeEmpty(); var v = new Vector3(); for ( int i = 0; i < a.count; i ++ ) expandByPoint( v.fromBufferAttribute( a, i ) ); return this; }
		public Vector3 getCenter( Vector3 t ) => t.addVectors( min, max ).multiplyScalar( 0.5 );
		public Box3 clone() { var b = new Box3(); b.min.copy( min ); b.max.copy( max ); return b; }
	}

	public sealed class Sphere
	{
		public readonly Vector3 center = new Vector3();
		public double radius = -1;
	}

	public class BufferGeometry
	{
		public IndexBuffer index;
		public readonly Dictionary<string, BufferAttribute> attributes = new Dictionary<string, BufferAttribute>();
		readonly List<string> order = new List<string>();
		public Box3 boundingBox;
		public Sphere boundingSphere;
		public string name = "";
		public string type = "BufferGeometry";

		public IEnumerable<string> attributeNames => order;
		public BufferGeometry setIndex( IList<int> idx ) { var a = new int[ idx.Count ]; idx.CopyTo( a, 0 ); index = new IndexBuffer( a ); return this; }
		public BufferGeometry setIndex( IndexBuffer idx ) { index = idx; return this; }
		public BufferAttribute getAttribute( string name ) { attributes.TryGetValue( name, out var a ); return a; }
		public BufferGeometry setAttribute( string name, BufferAttribute attr ) { if ( ! attributes.ContainsKey( name ) ) order.Add( name ); attributes[ name ] = attr; return this; }
		public BufferGeometry deleteAttribute( string name ) { attributes.Remove( name ); order.Remove( name ); return this; }
		public bool hasAttribute( string name ) => attributes.ContainsKey( name );

		static readonly Matrix3 _m3 = new Matrix3();
		static readonly Matrix4 _m1 = new Matrix4();

		public BufferGeometry applyMatrix4( Matrix4 m )
		{
			var pos = getAttribute( "position" );
			if ( pos != null ) pos.applyMatrix4( m );
			var nrm = getAttribute( "normal" );
			if ( nrm != null ) nrm.applyNormalMatrix( _m3.getNormalMatrix( m ) );
			var tan = getAttribute( "tangent" );
			if ( tan != null ) tan.transformDirection( m );
			if ( boundingBox != null ) computeBoundingBox();
			if ( boundingSphere != null ) computeBoundingSphere();
			return this;
		}

		public BufferGeometry applyQuaternion( Quaternion q ) => applyMatrix4( _m1.makeRotationFromQuaternion( q ) );
		public BufferGeometry rotateX( double a ) => applyMatrix4( _m1.makeRotationX( a ) );
		public BufferGeometry rotateY( double a ) => applyMatrix4( _m1.makeRotationY( a ) );
		public BufferGeometry rotateZ( double a ) => applyMatrix4( _m1.makeRotationZ( a ) );
		public BufferGeometry translate( double x, double y, double z ) => applyMatrix4( _m1.makeTranslation( x, y, z ) );
		public BufferGeometry scale( double x, double y, double z ) => applyMatrix4( _m1.makeScale( x, y, z ) );

		public void computeBoundingBox()
		{
			if ( boundingBox == null ) boundingBox = new Box3();
			var pos = getAttribute( "position" );
			if ( pos == null ) { boundingBox.makeEmpty(); return; }
			boundingBox.setFromBufferAttribute( pos );
		}

		public void computeBoundingSphere()
		{
			if ( boundingSphere == null ) boundingSphere = new Sphere();
			var pos = getAttribute( "position" );
			if ( pos == null ) return;
			var c = boundingSphere.center;
			new Box3().setFromBufferAttribute( pos ).getCenter( c );
			double r2 = 0; var v = new Vector3();
			for ( int i = 0; i < pos.count; i ++ ) r2 = Math.Max( r2, c.distanceToSquared( v.fromBufferAttribute( pos, i ) ) );
			boundingSphere.radius = Math.Sqrt( r2 );
		}

		// Area-weighted smooth normals (indexed) or flat face normals (non-indexed).
		public void computeVertexNormals()
		{
			var pos = getAttribute( "position" );
			if ( pos == null ) return;
			var nrm = getAttribute( "normal" );
			if ( nrm == null || nrm.count != pos.count )
			{
				nrm = new BufferAttribute( new float[ pos.count * 3 ], 3 );
				setAttribute( "normal", nrm );
			}
			else
			{
				for ( int i = 0; i < nrm.count; i ++ ) nrm.setXYZ( i, 0, 0, 0 );
			}

			var pA = new Vector3(); var pB = new Vector3(); var pC = new Vector3();
			var cb = new Vector3(); var ab = new Vector3();
			var nA = new Vector3(); var nB = new Vector3(); var nC = new Vector3();
			if ( index != null )
			{
				for ( int i = 0, il = index.count; i < il; i += 3 )
				{
					int vA = index.getX( i ), vB = index.getX( i + 1 ), vC = index.getX( i + 2 );
					pA.fromBufferAttribute( pos, vA ); pB.fromBufferAttribute( pos, vB ); pC.fromBufferAttribute( pos, vC );
					cb.subVectors( pC, pB ); ab.subVectors( pA, pB ); cb.cross( ab );
					nA.fromBufferAttribute( nrm, vA ); nB.fromBufferAttribute( nrm, vB ); nC.fromBufferAttribute( nrm, vC );
					nA.add( cb ); nB.add( cb ); nC.add( cb );
					nrm.setXYZ( vA, nA.x, nA.y, nA.z ); nrm.setXYZ( vB, nB.x, nB.y, nB.z ); nrm.setXYZ( vC, nC.x, nC.y, nC.z );
				}
			}
			else
			{
				for ( int i = 0, il = pos.count; i < il; i += 3 )
				{
					pA.fromBufferAttribute( pos, i ); pB.fromBufferAttribute( pos, i + 1 ); pC.fromBufferAttribute( pos, i + 2 );
					cb.subVectors( pC, pB ); ab.subVectors( pA, pB ); cb.cross( ab );
					nrm.setXYZ( i, cb.x, cb.y, cb.z ); nrm.setXYZ( i + 1, cb.x, cb.y, cb.z ); nrm.setXYZ( i + 2, cb.x, cb.y, cb.z );
				}
			}

			normalizeNormals();
		}

		public void normalizeNormals()
		{
			var n = attributes[ "normal" ];
			var v = new Vector3();
			for ( int i = 0, il = n.count; i < il; i ++ )
			{
				v.fromBufferAttribute( n, i ).normalize();
				n.setXYZ( i, v.x, v.y, v.z );
			}
		}

		public BufferGeometry toNonIndexed()
		{
			if ( index == null ) return this;
			var g = new BufferGeometry(); var idx = index;
			foreach ( var name in order )
			{
				var attr = attributes[ name ];
				int size = attr.itemSize; var o = new float[ idx.count * size ];
				for ( int i = 0; i < idx.count; i ++ )
				{
					int s = idx.getX( i );
					for ( int c = 0; c < size; c ++ ) o[ i * size + c ] = attr.array[ s * size + c ];
				}

				g.setAttribute( name, new BufferAttribute( o, size ) );
			}

			return g;
		}

		public BufferGeometry clone()
		{
			var g = new BufferGeometry { name = name };
			if ( index != null ) g.setIndex( index.clone() );
			foreach ( var n in order ) g.setAttribute( n, attributes[ n ].clone() );
			return g;
		}

		public int vertexCount => attributes[ "position" ].count;
		public int triangleCount => index != null ? index.count / 3 : vertexCount / 3;
	}

	public static class GeometryUtils
	{
		public static BufferAttribute mergeAttributes( List<BufferAttribute> attributes )
		{
			int itemSize = attributes[ 0 ].itemSize; int total = 0;
			foreach ( var a in attributes ) { if ( a.itemSize != itemSize ) return null; total += a.array.Length; }
			var o = new float[ total ]; int off = 0;
			foreach ( var a in attributes ) { Array.Copy( a.array, 0, o, off, a.array.Length ); off += a.array.Length; }
			return new BufferAttribute( o, itemSize );
		}

		public static BufferGeometry mergeGeometries( IList<BufferGeometry> geometries )
		{
			bool isIndexed = geometries[ 0 ].index != null;
			var names = new List<string>( geometries[ 0 ].attributeNames );
			var attrs = new Dictionary<string, List<BufferAttribute>>(); var attrOrder = new List<string>();
			var merged = new BufferGeometry();
			for ( int i = 0; i < geometries.Count; i ++ )
			{
				var g = geometries[ i ];
				if ( isIndexed != ( g.index != null ) ) return null;
				int n = 0;
				foreach ( var name in g.attributeNames )
				{
					if ( ! names.Contains( name ) ) return null;
					if ( ! attrs.ContainsKey( name ) ) { attrs[ name ] = new List<BufferAttribute>(); attrOrder.Add( name ); }
					attrs[ name ].Add( g.attributes[ name ] );
					n ++;
				}

				if ( n != names.Count ) return null;
			}

			if ( isIndexed )
			{
				int indexOffset = 0; var idx = new List<int>();
				foreach ( var g in geometries )
				{
					for ( int j = 0; j < g.index.count; j ++ ) idx.Add( g.index.getX( j ) + indexOffset );
					indexOffset += g.attributes[ "position" ].count;
				}

				merged.setIndex( idx );
			}

			foreach ( var name in attrOrder )
			{
				var a = mergeAttributes( attrs[ name ] );
				if ( a == null ) return null;
				merged.setAttribute( name, a );
			}

			return merged;
		}
	}
}
