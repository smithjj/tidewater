using System;
using System.Collections.Generic;

// Port of src/engine/math/CatmullRomCurve3.js (Curve base: arc-length table of 200 divisions, Frenet / parallel-transport frames).
namespace Tidewater.Engine
{
	public abstract class Curve
	{
		public int arcLengthDivisions = 200;
		double[] cacheArcLengths;

		public abstract Vector3 getPoint( double t, Vector3 target );
		public Vector3 getPoint( double t ) => getPoint( t, new Vector3() );
		public Vector3 getPointAt( double u, Vector3 target ) => getPoint( getUtoTmapping( u ), target );
		public Vector3 getPointAt( double u ) => getPointAt( u, new Vector3() );

		public List<Vector3> getPoints( int divisions = 5 )
		{
			var pts = new List<Vector3>();
			for ( int d = 0; d <= divisions; d ++ ) pts.Add( getPoint( ( double ) d / divisions ) );
			return pts;
		}

		public List<Vector3> getSpacedPoints( int divisions = 5 )
		{
			var pts = new List<Vector3>();
			for ( int d = 0; d <= divisions; d ++ ) pts.Add( getPointAt( ( double ) d / divisions ) );
			return pts;
		}

		public double getLength() { var l = getLengths(); return l[ l.Length - 1 ]; }

		public double[] getLengths()
		{
			int divisions = arcLengthDivisions;
			if ( cacheArcLengths != null && cacheArcLengths.Length == divisions + 1 ) return cacheArcLengths;
			var cache = new double[ divisions + 1 ];
			var last = getPoint( 0 ); double sum = 0;
			for ( int p = 1; p <= divisions; p ++ )
			{
				var cur = getPoint( ( double ) p / divisions );
				sum += cur.distanceTo( last );
				cache[ p ] = sum;
				last = cur;
			}

			cacheArcLengths = cache;
			return cache;
		}

		// Arc-length fraction u -> curve parameter t.
		public double getUtoTmapping( double u )
		{
			var L = getLengths(); int n = L.Length;
			double target = u * L[ n - 1 ];
			int lo = 0, hi = n - 1;
			while ( lo <= hi )
			{
				int i0 = ( int ) Math.Floor( lo + ( hi - lo ) / 2.0 );
				double c = L[ i0 ] - target;
				if ( c < 0 ) lo = i0 + 1;
				else if ( c > 0 ) hi = i0 - 1;
				else { hi = i0; break; }
			}

			int i = hi;
			if ( L[ i ] == target ) return ( double ) i / ( n - 1 );
			double before = L[ i ], after = L[ i + 1 ];
			return ( i + ( target - before ) / ( after - before ) ) / ( n - 1 );
		}

		public Vector3 getTangent( double t, Vector3 target )
		{
			double delta = 0.0001;
			double t1 = Math.Max( 0, t - delta ), t2 = Math.Min( 1, t + delta );
			var a = getPoint( t1 ); var b = getPoint( t2 );
			return target.copy( b ).sub( a ).normalize();
		}

		public Vector3 getTangentAt( double u, Vector3 target ) => getTangent( getUtoTmapping( u ), target );

		public struct Frames { public List<Vector3> tangents, normals, binormals; }

		// Parallel-transport frames (rotation-minimizing), as used by TubeGeometry.
		public Frames computeFrenetFrames( int segments, bool closed )
		{
			var normal = new Vector3(); var tangents = new List<Vector3>(); var normals = new List<Vector3>(); var binormals = new List<Vector3>();
			var vec = new Vector3(); var mat = new Matrix4();
			for ( int i = 0; i <= segments; i ++ ) tangents.Add( getTangentAt( ( double ) i / segments, new Vector3() ) );
			normals.Add( new Vector3() ); binormals.Add( new Vector3() );
			double min = double.MaxValue;
			double tx = Math.Abs( tangents[ 0 ].x ), ty = Math.Abs( tangents[ 0 ].y ), tz = Math.Abs( tangents[ 0 ].z );
			if ( tx <= min ) { min = tx; normal.set( 1, 0, 0 ); }
			if ( ty <= min ) { min = ty; normal.set( 0, 1, 0 ); }
			if ( tz <= min ) normal.set( 0, 0, 1 );
			vec.crossVectors( tangents[ 0 ], normal ).normalize();
			normals[ 0 ].crossVectors( tangents[ 0 ], vec );
			binormals[ 0 ].crossVectors( tangents[ 0 ], normals[ 0 ] );
			for ( int i = 1; i <= segments; i ++ )
			{
				normals.Add( normals[ i - 1 ].clone() );
				binormals.Add( binormals[ i - 1 ].clone() );
				vec.crossVectors( tangents[ i - 1 ], tangents[ i ] );
				if ( vec.length() > JS.EPSILON )
				{
					vec.normalize();
					double theta = Math.Acos( Math.Max( -1, Math.Min( 1, tangents[ i - 1 ].dot( tangents[ i ] ) ) ) );
					normals[ i ].applyMatrix4( mat.makeRotationAxis( vec, theta ) );
				}

				binormals[ i ].crossVectors( tangents[ i ], normals[ i ] );
			}

			if ( closed )
			{
				double theta = Math.Acos( Math.Max( -1, Math.Min( 1, normals[ 0 ].dot( normals[ segments ] ) ) ) ) / segments;
				if ( tangents[ 0 ].dot( vec.crossVectors( normals[ 0 ], normals[ segments ] ) ) > 0 ) theta = -theta;
				for ( int i = 1; i <= segments; i ++ )
				{
					normals[ i ].applyMatrix4( mat.makeRotationAxis( tangents[ i ], theta * i ) );
					binormals[ i ].crossVectors( tangents[ i ], normals[ i ] );
				}
			}

			return new Frames { tangents = tangents, normals = normals, binormals = binormals };
		}
	}

	public sealed class CatmullRomCurve3 : Curve
	{
		public List<Vector3> points; public bool closed; public string curveType; public double tension;
		static readonly double[] _px = new double[ 4 ], _py = new double[ 4 ], _pz = new double[ 4 ];
		static readonly Vector3 _t0 = new Vector3(), _t3 = new Vector3();

		public CatmullRomCurve3( List<Vector3> points = null, bool closed = false, string curveType = "centripetal", double tension = 0.5 )
		{
			this.points = points ?? new List<Vector3>(); this.closed = closed; this.curveType = curveType; this.tension = tension;
		}

		// Cubic c0 + c1 t + c2 t^2 + c3 t^3 from Hermite endpoints / tangents.
		static void Hermite( double[] o, double x0, double x1, double t0, double t1 )
		{
			o[ 0 ] = x0;
			o[ 1 ] = t0;
			o[ 2 ] = -3 * x0 + 3 * x1 - 2 * t0 - t1;
			o[ 3 ] = 2 * x0 - 2 * x1 + t0 + t1;
		}

		static void UniformCR( double[] o, double x0, double x1, double x2, double x3, double tension ) => Hermite( o, x1, x2, tension * ( x2 - x0 ), tension * ( x3 - x1 ) );

		static void NonuniformCR( double[] o, double x0, double x1, double x2, double x3, double dt0, double dt1, double dt2 )
		{
			double t1 = ( x1 - x0 ) / dt0 - ( x2 - x0 ) / ( dt0 + dt1 ) + ( x2 - x1 ) / dt1;
			double t2 = ( x2 - x1 ) / dt1 - ( x3 - x1 ) / ( dt1 + dt2 ) + ( x3 - x2 ) / dt2;
			t1 *= dt1;
			t2 *= dt1;
			Hermite( o, x1, x2, t1, t2 );
		}

		static double EvalPoly( double[] c, double t ) => c[ 0 ] + t * ( c[ 1 ] + t * ( c[ 2 ] + t * c[ 3 ] ) );

		public override Vector3 getPoint( double t, Vector3 target )
		{
			var pts = points; int l = pts.Count;
			double p = ( l - ( closed ? 0 : 1 ) ) * t;
			int i = ( int ) Math.Floor( p ); double w = p - i;
			if ( closed ) i += i > 0 ? 0 : ( ( int ) Math.Floor( Math.Abs( ( double ) i ) / l ) + 1 ) * l;
			else if ( w == 0 && i == l - 1 ) { i = l - 2; w = 1; }
			Vector3 p0, p3;
			if ( closed || i > 0 ) p0 = pts[ ( i - 1 ) % l ];
			else p0 = _t0.subVectors( pts[ 0 ], pts[ 1 ] ).add( pts[ 0 ] );
			var p1 = pts[ i % l ]; var p2 = pts[ ( i + 1 ) % l ];
			if ( closed || i + 2 < l ) p3 = pts[ ( i + 2 ) % l ];
			else p3 = _t3.subVectors( pts[ l - 1 ], pts[ l - 2 ] ).add( pts[ l - 1 ] );

			if ( curveType == "centripetal" || curveType == "chordal" )
			{
				double pw = curveType == "chordal" ? 0.5 : 0.25;
				double dt0 = Math.Pow( p0.distanceToSquared( p1 ), pw );
				double dt1 = Math.Pow( p1.distanceToSquared( p2 ), pw );
				double dt2 = Math.Pow( p2.distanceToSquared( p3 ), pw );
				if ( dt1 < 1e-4 ) dt1 = 1.0;
				if ( dt0 < 1e-4 ) dt0 = dt1;
				if ( dt2 < 1e-4 ) dt2 = dt1;
				NonuniformCR( _px, p0.x, p1.x, p2.x, p3.x, dt0, dt1, dt2 );
				NonuniformCR( _py, p0.y, p1.y, p2.y, p3.y, dt0, dt1, dt2 );
				NonuniformCR( _pz, p0.z, p1.z, p2.z, p3.z, dt0, dt1, dt2 );
			}
			else
			{
				UniformCR( _px, p0.x, p1.x, p2.x, p3.x, tension );
				UniformCR( _py, p0.y, p1.y, p2.y, p3.y, tension );
				UniformCR( _pz, p0.z, p1.z, p2.z, p3.z, tension );
			}

			return target.set( EvalPoly( _px, w ), EvalPoly( _py, w ), EvalPoly( _pz, w ) );
		}
	}
}
