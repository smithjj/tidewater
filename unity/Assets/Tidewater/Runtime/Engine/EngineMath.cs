using System;

// Port of the math of src/engine/math (three.js-compatible): Vector2, Vector3, Matrix3, Matrix4, Quaternion, Euler, MathUtils.
// Doubles throughout (JS numbers), method names as in the JS in PascalCase, and the same operation order, so procedural geometry built
// with them (the boat, props, the village) matches the JS. `Hypot` is V8's Math.hypot (scaled, compensated summation).
namespace Tidewater.Engine
{
	public static class JS
	{
		public const double EPSILON = 2.220446049250313e-16;   // Number.EPSILON
		public static double Round( double x ) => Math.Floor( x + 0.5 );      // Math.round
		public static double Trunc( double x ) => Math.Truncate( x );
		public static double Sign( double x ) => double.IsNaN( x ) ? double.NaN : ( x > 0 ? 1 : ( x < 0 ? -1 : x ) );   // Math.sign
		public static double Or( double x, double fallback ) => ( x == 0 || double.IsNaN( x ) ) ? fallback : x;           // x || fallback
		public static double Hypot( double a, double b ) => HypotN( a, b, 0, 2 );
		public static double Hypot( double a, double b, double c ) => HypotN( a, b, c, 3 );

		// V8: scale by the largest magnitude and sum the squares with Kahan compensation
		static double HypotN( double a, double b, double c, int n )
		{
			double ma = Math.Abs( a ), mb = Math.Abs( b ), mc = n > 2 ? Math.Abs( c ) : 0;
			double max = Math.Max( ma, Math.Max( mb, mc ) );
			if ( double.IsInfinity( ma ) || double.IsInfinity( mb ) || double.IsInfinity( mc ) ) return double.PositiveInfinity;
			if ( double.IsNaN( a ) || double.IsNaN( b ) || ( n > 2 && double.IsNaN( c ) ) ) return double.NaN;
			if ( max == 0 ) return 0;
			double sum = 0, comp = 0;
			for ( int i = 0; i < n; i ++ )
			{
				double v = i == 0 ? ma : ( i == 1 ? mb : mc );
				double q = v / max;
				double summand = q * q - comp;
				double prelim = sum + summand;
				comp = ( prelim - sum ) - summand;
				sum = prelim;
			}

			return Math.Sqrt( sum ) * max;
		}

		public static double Clamp( double v, double lo, double hi ) => Math.Max( lo, Math.Min( hi, v ) );
	}

	public static class MathUtils
	{
		public const double DEG2RAD = Math.PI / 180, RAD2DEG = 180 / Math.PI;
		public static double clamp( double v, double lo, double hi ) => Math.Max( lo, Math.Min( hi, v ) );
		public static double euclideanModulo( double n, double m ) => ( ( n % m ) + m ) % m;
		public static double mapLinear( double x, double a1, double a2, double b1, double b2 ) => b1 + ( x - a1 ) * ( b2 - b1 ) / ( a2 - a1 );
		public static double inverseLerp( double x, double y, double v ) => x != y ? ( v - x ) / ( y - x ) : 0;
		public static double lerp( double x, double y, double t ) => ( 1 - t ) * x + t * y;
		public static double damp( double x, double y, double lambda, double dt ) => lerp( x, y, 1 - Math.Exp( -lambda * dt ) );
		public static double smoothstep( double x, double min, double max )
		{
			if ( x <= min ) return 0;
			if ( x >= max ) return 1;
			x = ( x - min ) / ( max - min );
			return x * x * ( 3 - 2 * x );
		}

		public static double smootherstep( double x, double min, double max )
		{
			if ( x <= min ) return 0;
			if ( x >= max ) return 1;
			x = ( x - min ) / ( max - min );
			return x * x * x * ( x * ( x * 6 - 15 ) + 10 );
		}

		public static double degToRad( double d ) => d * DEG2RAD;
		public static double radToDeg( double r ) => r * RAD2DEG;
	}

	public sealed class Vector2
	{
		public double x, y;
		public Vector2( double x = 0, double y = 0 ) { this.x = x; this.y = y; }
		public Vector2 set( double x, double y ) { this.x = x; this.y = y; return this; }
		public Vector2 clone() => new Vector2( x, y );
		public Vector2 copy( Vector2 v ) { x = v.x; y = v.y; return this; }
		public Vector2 add( Vector2 v ) { x += v.x; y += v.y; return this; }
		public Vector2 addVectors( Vector2 a, Vector2 b ) { x = a.x + b.x; y = a.y + b.y; return this; }
		public Vector2 addScaledVector( Vector2 v, double s ) { x += v.x * s; y += v.y * s; return this; }
		public Vector2 sub( Vector2 v ) { x -= v.x; y -= v.y; return this; }
		public Vector2 subVectors( Vector2 a, Vector2 b ) { x = a.x - b.x; y = a.y - b.y; return this; }
		public Vector2 multiplyScalar( double s ) { x *= s; y *= s; return this; }
		public Vector2 divideScalar( double s ) => multiplyScalar( 1 / s );
		public double dot( Vector2 v ) => x * v.x + y * v.y;
		public double cross( Vector2 v ) => x * v.y - y * v.x;
		public double lengthSq() => x * x + y * y;
		public double length() => Math.Sqrt( x * x + y * y );
		public Vector2 normalize() { double l = length(); return divideScalar( l != 0 ? l : 1 ); }
		public double distanceTo( Vector2 v ) => Math.Sqrt( distanceToSquared( v ) );
		public double distanceToSquared( Vector2 v ) { double dx = x - v.x, dy = y - v.y; return dx * dx + dy * dy; }
		public Vector2 lerp( Vector2 v, double a ) { x += ( v.x - x ) * a; y += ( v.y - y ) * a; return this; }
		public Vector2 lerpVectors( Vector2 a, Vector2 b, double t ) { x = a.x + ( b.x - a.x ) * t; y = a.y + ( b.y - a.y ) * t; return this; }
		public Vector2 applyMatrix3( Matrix3 m )
		{
			double X = x, Y = y; var e = m.elements;
			x = e[ 0 ] * X + e[ 3 ] * Y + e[ 6 ];
			y = e[ 1 ] * X + e[ 4 ] * Y + e[ 7 ];
			return this;
		}
	}

	public sealed class Vector3
	{
		public double x, y, z;
		public Vector3( double x = 0, double y = 0, double z = 0 ) { this.x = x; this.y = y; this.z = z; }
		public Vector3 set( double x, double y, double z ) { this.x = x; this.y = y; this.z = z; return this; }
		public Vector3 setScalar( double s ) { x = s; y = s; z = s; return this; }
		public Vector3 clone() => new Vector3( x, y, z );
		public Vector3 copy( Vector3 v ) { x = v.x; y = v.y; z = v.z; return this; }
		public Vector3 add( Vector3 v ) { x += v.x; y += v.y; z += v.z; return this; }
		public Vector3 addScalar( double s ) { x += s; y += s; z += s; return this; }
		public Vector3 addVectors( Vector3 a, Vector3 b ) { x = a.x + b.x; y = a.y + b.y; z = a.z + b.z; return this; }
		public Vector3 addScaledVector( Vector3 v, double s ) { x += v.x * s; y += v.y * s; z += v.z * s; return this; }
		public Vector3 sub( Vector3 v ) { x -= v.x; y -= v.y; z -= v.z; return this; }
		public Vector3 subScalar( double s ) { x -= s; y -= s; z -= s; return this; }
		public Vector3 subVectors( Vector3 a, Vector3 b ) { x = a.x - b.x; y = a.y - b.y; z = a.z - b.z; return this; }
		public Vector3 multiply( Vector3 v ) { x *= v.x; y *= v.y; z *= v.z; return this; }
		public Vector3 multiplyScalar( double s ) { x *= s; y *= s; z *= s; return this; }
		public Vector3 multiplyVectors( Vector3 a, Vector3 b ) { x = a.x * b.x; y = a.y * b.y; z = a.z * b.z; return this; }
		public Vector3 divide( Vector3 v ) { x /= v.x; y /= v.y; z /= v.z; return this; }
		public Vector3 divideScalar( double s ) => multiplyScalar( 1 / s );

		public Vector3 applyEuler( Euler e ) => applyQuaternion( new Quaternion().setFromEuler( e ) );
		public Vector3 applyAxisAngle( Vector3 axis, double angle ) => applyQuaternion( new Quaternion().setFromAxisAngle( axis, angle ) );

		public Vector3 applyMatrix3( Matrix3 m )
		{
			double X = x, Y = y, Z = z; var e = m.elements;
			x = e[ 0 ] * X + e[ 3 ] * Y + e[ 6 ] * Z;
			y = e[ 1 ] * X + e[ 4 ] * Y + e[ 7 ] * Z;
			z = e[ 2 ] * X + e[ 5 ] * Y + e[ 8 ] * Z;
			return this;
		}

		public Vector3 applyNormalMatrix( Matrix3 m ) => applyMatrix3( m ).normalize();

		public Vector3 applyMatrix4( Matrix4 m )
		{
			double X = x, Y = y, Z = z; var e = m.elements;
			double w = 1 / ( e[ 3 ] * X + e[ 7 ] * Y + e[ 11 ] * Z + e[ 15 ] );
			x = ( e[ 0 ] * X + e[ 4 ] * Y + e[ 8 ] * Z + e[ 12 ] ) * w;
			y = ( e[ 1 ] * X + e[ 5 ] * Y + e[ 9 ] * Z + e[ 13 ] ) * w;
			z = ( e[ 2 ] * X + e[ 6 ] * Y + e[ 10 ] * Z + e[ 14 ] ) * w;
			return this;
		}

		public Vector3 applyQuaternion( Quaternion q )
		{
			// v' = v + 2w(q x v) + 2 q x (q x v)
			double vx = x, vy = y, vz = z, qx = q.x, qy = q.y, qz = q.z, qw = q.w;
			double tx = 2 * ( qy * vz - qz * vy ), ty = 2 * ( qz * vx - qx * vz ), tz = 2 * ( qx * vy - qy * vx );
			x = vx + qw * tx + qy * tz - qz * ty;
			y = vy + qw * ty + qz * tx - qx * tz;
			z = vz + qw * tz + qx * ty - qy * tx;
			return this;
		}

		public Vector3 transformDirection( Matrix4 m )
		{
			double X = x, Y = y, Z = z; var e = m.elements;
			x = e[ 0 ] * X + e[ 4 ] * Y + e[ 8 ] * Z;
			y = e[ 1 ] * X + e[ 5 ] * Y + e[ 9 ] * Z;
			z = e[ 2 ] * X + e[ 6 ] * Y + e[ 10 ] * Z;
			return normalize();
		}

		public Vector3 min( Vector3 v ) { x = Math.Min( x, v.x ); y = Math.Min( y, v.y ); z = Math.Min( z, v.z ); return this; }
		public Vector3 max( Vector3 v ) { x = Math.Max( x, v.x ); y = Math.Max( y, v.y ); z = Math.Max( z, v.z ); return this; }
		public Vector3 clampScalar( double lo, double hi ) { x = Math.Max( lo, Math.Min( hi, x ) ); y = Math.Max( lo, Math.Min( hi, y ) ); z = Math.Max( lo, Math.Min( hi, z ) ); return this; }
		public Vector3 clampLength( double lo, double hi ) { double l = length(); return divideScalar( l != 0 ? l : 1 ).multiplyScalar( Math.Max( lo, Math.Min( hi, l ) ) ); }
		public Vector3 negate() { x = -x; y = -y; z = -z; return this; }
		public double dot( Vector3 v ) => x * v.x + y * v.y + z * v.z;
		public double lengthSq() => x * x + y * y + z * z;
		public double length() => Math.Sqrt( x * x + y * y + z * z );
		public Vector3 normalize() { double l = length(); return divideScalar( l != 0 ? l : 1 ); }
		public Vector3 setLength( double l ) => normalize().multiplyScalar( l );
		public Vector3 lerp( Vector3 v, double a ) { x += ( v.x - x ) * a; y += ( v.y - y ) * a; z += ( v.z - z ) * a; return this; }
		public Vector3 lerpVectors( Vector3 a, Vector3 b, double t ) { x = a.x + ( b.x - a.x ) * t; y = a.y + ( b.y - a.y ) * t; z = a.z + ( b.z - a.z ) * t; return this; }
		public Vector3 cross( Vector3 v ) => crossVectors( this, v );

		public Vector3 crossVectors( Vector3 a, Vector3 b )
		{
			double ax = a.x, ay = a.y, az = a.z, bx = b.x, by = b.y, bz = b.z;
			x = ay * bz - az * by;
			y = az * bx - ax * bz;
			z = ax * by - ay * bx;
			return this;
		}

		public Vector3 projectOnVector( Vector3 v )
		{
			double d = v.lengthSq();
			if ( d == 0 ) return set( 0, 0, 0 );
			double s = v.dot( this ) / d;
			return copy( v ).multiplyScalar( s );
		}

		public Vector3 projectOnPlane( Vector3 n ) { double nl = n.lengthSq(); double d = dot( n ) / ( nl != 0 ? nl : 1 ); return addScaledVector( n, -d ); }
		public Vector3 reflect( Vector3 n ) => addScaledVector( n, -2 * dot( n ) );

		public double angleTo( Vector3 v )
		{
			double d = Math.Sqrt( lengthSq() * v.lengthSq() );
			if ( d == 0 ) return Math.PI / 2;
			return Math.Acos( Math.Max( -1, Math.Min( 1, dot( v ) / d ) ) );
		}

		public double distanceTo( Vector3 v ) => Math.Sqrt( distanceToSquared( v ) );
		public double distanceToSquared( Vector3 v ) { double dx = x - v.x, dy = y - v.y, dz = z - v.z; return dx * dx + dy * dy + dz * dz; }

		public Vector3 setFromSphericalCoords( double r, double phi, double theta )
		{
			double sp = Math.Sin( phi ) * r;
			x = sp * Math.Sin( theta );
			y = Math.Cos( phi ) * r;
			z = sp * Math.Cos( theta );
			return this;
		}

		public Vector3 setFromMatrixPosition( Matrix4 m ) { var e = m.elements; x = e[ 12 ]; y = e[ 13 ]; z = e[ 14 ]; return this; }
		public Vector3 setFromMatrixColumn( Matrix4 m, int i ) { var e = m.elements; x = e[ i * 4 ]; y = e[ i * 4 + 1 ]; z = e[ i * 4 + 2 ]; return this; }
		public Vector3 setFromMatrixScale( Matrix4 m )
		{
			double sx = setFromMatrixColumn( m, 0 ).length();
			double sy = setFromMatrixColumn( m, 1 ).length();
			double sz = setFromMatrixColumn( m, 2 ).length();
			return set( sx, sy, sz );
		}

		public Vector3 setFromEuler( Euler e ) { x = e.x; y = e.y; z = e.z; return this; }
		public bool equals( Vector3 v ) => v.x == x && v.y == y && v.z == z;
		public double getComponent( int i ) => i == 0 ? x : ( i == 1 ? y : z );
		public Vector3 setComponent( int i, double v ) { if ( i == 0 ) x = v; else if ( i == 1 ) y = v; else z = v; return this; }
		public UnityEngine.Vector3 ToUnity() => new UnityEngine.Vector3( ( float ) x, ( float ) y, ( float ) z );
	}

	public sealed class Matrix3
	{
		public readonly double[] elements = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
		public Matrix3() { }

		// row-major arguments
		public Matrix3 set( double n11, double n12, double n13, double n21, double n22, double n23, double n31, double n32, double n33 )
		{
			var e = elements;
			e[ 0 ] = n11; e[ 1 ] = n21; e[ 2 ] = n31;
			e[ 3 ] = n12; e[ 4 ] = n22; e[ 5 ] = n32;
			e[ 6 ] = n13; e[ 7 ] = n23; e[ 8 ] = n33;
			return this;
		}

		public Matrix3 setFromMatrix4( Matrix4 m )
		{
			var e = m.elements;
			return set( e[ 0 ], e[ 4 ], e[ 8 ], e[ 1 ], e[ 5 ], e[ 9 ], e[ 2 ], e[ 6 ], e[ 10 ] );
		}

		public Matrix3 invert()
		{
			var e = elements;
			double n11 = e[ 0 ], n21 = e[ 1 ], n31 = e[ 2 ], n12 = e[ 3 ], n22 = e[ 4 ], n32 = e[ 5 ], n13 = e[ 6 ], n23 = e[ 7 ], n33 = e[ 8 ];
			double t11 = n33 * n22 - n32 * n23, t12 = n32 * n13 - n33 * n12, t13 = n23 * n12 - n22 * n13;
			double det = n11 * t11 + n21 * t12 + n31 * t13;
			if ( det == 0 ) return set( 0, 0, 0, 0, 0, 0, 0, 0, 0 );
			double id = 1 / det;
			e[ 0 ] = t11 * id; e[ 1 ] = ( n31 * n23 - n33 * n21 ) * id; e[ 2 ] = ( n32 * n21 - n31 * n22 ) * id;
			e[ 3 ] = t12 * id; e[ 4 ] = ( n33 * n11 - n31 * n13 ) * id; e[ 5 ] = ( n31 * n12 - n32 * n11 ) * id;
			e[ 6 ] = t13 * id; e[ 7 ] = ( n21 * n13 - n23 * n11 ) * id; e[ 8 ] = ( n22 * n11 - n21 * n12 ) * id;
			return this;
		}

		public Matrix3 transpose()
		{
			var m = elements;
			double t;
			t = m[ 1 ]; m[ 1 ] = m[ 3 ]; m[ 3 ] = t;
			t = m[ 2 ]; m[ 2 ] = m[ 6 ]; m[ 6 ] = t;
			t = m[ 5 ]; m[ 5 ] = m[ 7 ]; m[ 7 ] = t;
			return this;
		}

		public Matrix3 getNormalMatrix( Matrix4 m4 ) => setFromMatrix4( m4 ).invert().transpose();
	}

	public sealed class Matrix4
	{
		public readonly double[] elements = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
		static readonly Vector3 _v1 = new Vector3(), _x = new Vector3(), _y = new Vector3(), _z = new Vector3(), _one = new Vector3( 1, 1, 1 ), _zero = new Vector3();
		static readonly Quaternion _q = new Quaternion();

		public Matrix4() { }

		// row-major arguments
		public Matrix4 set( double n11, double n12, double n13, double n14, double n21, double n22, double n23, double n24, double n31, double n32, double n33, double n34, double n41, double n42, double n43, double n44 )
		{
			var e = elements;
			e[ 0 ] = n11; e[ 4 ] = n12; e[ 8 ] = n13; e[ 12 ] = n14;
			e[ 1 ] = n21; e[ 5 ] = n22; e[ 9 ] = n23; e[ 13 ] = n24;
			e[ 2 ] = n31; e[ 6 ] = n32; e[ 10 ] = n33; e[ 14 ] = n34;
			e[ 3 ] = n41; e[ 7 ] = n42; e[ 11 ] = n43; e[ 15 ] = n44;
			return this;
		}

		public Matrix4 identity() => set( 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 );
		public Matrix4 clone() { var m = new Matrix4(); Array.Copy( elements, m.elements, 16 ); return m; }
		public Matrix4 copy( Matrix4 m ) { Array.Copy( m.elements, elements, 16 ); return this; }
		public Matrix4 copyPosition( Matrix4 m ) { var e = elements; var s = m.elements; e[ 12 ] = s[ 12 ]; e[ 13 ] = s[ 13 ]; e[ 14 ] = s[ 14 ]; return this; }
		public Matrix4 makeBasis( Vector3 x, Vector3 y, Vector3 z ) => set( x.x, y.x, z.x, 0, x.y, y.y, z.y, 0, x.z, y.z, z.z, 0, 0, 0, 0, 1 );

		public Matrix4 extractRotation( Matrix4 m )
		{
			var e = elements; var me = m.elements;
			double sx = 1 / _v1.setFromMatrixColumn( m, 0 ).length();
			double sy = 1 / _v1.setFromMatrixColumn( m, 1 ).length();
			double sz = 1 / _v1.setFromMatrixColumn( m, 2 ).length();
			e[ 0 ] = me[ 0 ] * sx; e[ 1 ] = me[ 1 ] * sx; e[ 2 ] = me[ 2 ] * sx; e[ 3 ] = 0;
			e[ 4 ] = me[ 4 ] * sy; e[ 5 ] = me[ 5 ] * sy; e[ 6 ] = me[ 6 ] * sy; e[ 7 ] = 0;
			e[ 8 ] = me[ 8 ] * sz; e[ 9 ] = me[ 9 ] * sz; e[ 10 ] = me[ 10 ] * sz; e[ 11 ] = 0;
			e[ 12 ] = 0; e[ 13 ] = 0; e[ 14 ] = 0; e[ 15 ] = 1;
			return this;
		}

		public Matrix4 makeRotationFromEuler( Euler e ) => compose( _zero, _q.setFromEuler( e, false ), _one );
		public Matrix4 makeRotationFromQuaternion( Quaternion q ) => compose( _zero, q, _one );

		// Rotation so that local +Z points from `target` toward `eye` (three.js convention).
		public Matrix4 lookAt( Vector3 eye, Vector3 target, Vector3 up )
		{
			var e = elements;
			_z.subVectors( eye, target );
			if ( _z.lengthSq() == 0 ) _z.z = 1;
			_z.normalize();
			_x.crossVectors( up, _z );
			if ( _x.lengthSq() == 0 )
			{
				if ( Math.Abs( up.z ) == 1 ) _z.x += 0.0001; else _z.z += 0.0001;
				_z.normalize();
				_x.crossVectors( up, _z );
			}

			_x.normalize();
			_y.crossVectors( _z, _x );
			e[ 0 ] = _x.x; e[ 4 ] = _y.x; e[ 8 ] = _z.x;
			e[ 1 ] = _x.y; e[ 5 ] = _y.y; e[ 9 ] = _z.y;
			e[ 2 ] = _x.z; e[ 6 ] = _y.z; e[ 10 ] = _z.z;
			return this;
		}

		public Matrix4 multiply( Matrix4 m ) => multiplyMatrices( this, m );
		public Matrix4 premultiply( Matrix4 m ) => multiplyMatrices( m, this );

		public Matrix4 multiplyMatrices( Matrix4 a, Matrix4 b )
		{
			var ae = a.elements; var be = b.elements; var te = elements;
			double a11 = ae[ 0 ], a12 = ae[ 4 ], a13 = ae[ 8 ], a14 = ae[ 12 ];
			double a21 = ae[ 1 ], a22 = ae[ 5 ], a23 = ae[ 9 ], a24 = ae[ 13 ];
			double a31 = ae[ 2 ], a32 = ae[ 6 ], a33 = ae[ 10 ], a34 = ae[ 14 ];
			double a41 = ae[ 3 ], a42 = ae[ 7 ], a43 = ae[ 11 ], a44 = ae[ 15 ];
			double b11 = be[ 0 ], b12 = be[ 4 ], b13 = be[ 8 ], b14 = be[ 12 ];
			double b21 = be[ 1 ], b22 = be[ 5 ], b23 = be[ 9 ], b24 = be[ 13 ];
			double b31 = be[ 2 ], b32 = be[ 6 ], b33 = be[ 10 ], b34 = be[ 14 ];
			double b41 = be[ 3 ], b42 = be[ 7 ], b43 = be[ 11 ], b44 = be[ 15 ];

			te[ 0 ] = a11 * b11 + a12 * b21 + a13 * b31 + a14 * b41;
			te[ 4 ] = a11 * b12 + a12 * b22 + a13 * b32 + a14 * b42;
			te[ 8 ] = a11 * b13 + a12 * b23 + a13 * b33 + a14 * b43;
			te[ 12 ] = a11 * b14 + a12 * b24 + a13 * b34 + a14 * b44;
			te[ 1 ] = a21 * b11 + a22 * b21 + a23 * b31 + a24 * b41;
			te[ 5 ] = a21 * b12 + a22 * b22 + a23 * b32 + a24 * b42;
			te[ 9 ] = a21 * b13 + a22 * b23 + a23 * b33 + a24 * b43;
			te[ 13 ] = a21 * b14 + a22 * b24 + a23 * b34 + a24 * b44;
			te[ 2 ] = a31 * b11 + a32 * b21 + a33 * b31 + a34 * b41;
			te[ 6 ] = a31 * b12 + a32 * b22 + a33 * b32 + a34 * b42;
			te[ 10 ] = a31 * b13 + a32 * b23 + a33 * b33 + a34 * b43;
			te[ 14 ] = a31 * b14 + a32 * b24 + a33 * b34 + a34 * b44;
			te[ 3 ] = a41 * b11 + a42 * b21 + a43 * b31 + a44 * b41;
			te[ 7 ] = a41 * b12 + a42 * b22 + a43 * b32 + a44 * b42;
			te[ 11 ] = a41 * b13 + a42 * b23 + a43 * b33 + a44 * b43;
			te[ 15 ] = a41 * b14 + a42 * b24 + a43 * b34 + a44 * b44;
			return this;
		}

		public Matrix4 multiplyScalar( double s ) { for ( int i = 0; i < 16; i ++ ) elements[ i ] *= s; return this; }

		public double determinant()
		{
			var e = elements;
			double n11 = e[ 0 ], n12 = e[ 4 ], n13 = e[ 8 ], n14 = e[ 12 ];
			double n21 = e[ 1 ], n22 = e[ 5 ], n23 = e[ 9 ], n24 = e[ 13 ];
			double n31 = e[ 2 ], n32 = e[ 6 ], n33 = e[ 10 ], n34 = e[ 14 ];
			double n41 = e[ 3 ], n42 = e[ 7 ], n43 = e[ 11 ], n44 = e[ 15 ];
			double s0 = n33 * n44 - n34 * n43, s1 = n32 * n44 - n34 * n42, s2 = n32 * n43 - n33 * n42;
			double s3 = n31 * n44 - n34 * n41, s4 = n31 * n43 - n33 * n41, s5 = n31 * n42 - n32 * n41;
			return n11 * ( n22 * s0 - n23 * s1 + n24 * s2 ) -
				n12 * ( n21 * s0 - n23 * s3 + n24 * s4 ) +
				n13 * ( n21 * s1 - n22 * s3 + n24 * s5 ) -
				n14 * ( n21 * s2 - n22 * s4 + n23 * s5 );
		}

		public Matrix4 transpose()
		{
			var e = elements;
			double t;
			t = e[ 1 ]; e[ 1 ] = e[ 4 ]; e[ 4 ] = t;
			t = e[ 2 ]; e[ 2 ] = e[ 8 ]; e[ 8 ] = t;
			t = e[ 6 ]; e[ 6 ] = e[ 9 ]; e[ 9 ] = t;
			t = e[ 3 ]; e[ 3 ] = e[ 12 ]; e[ 12 ] = t;
			t = e[ 7 ]; e[ 7 ] = e[ 13 ]; e[ 13 ] = t;
			t = e[ 11 ]; e[ 11 ] = e[ 14 ]; e[ 14 ] = t;
			return this;
		}

		public Matrix4 setPosition( Vector3 v ) { var e = elements; e[ 12 ] = v.x; e[ 13 ] = v.y; e[ 14 ] = v.z; return this; }
		public Matrix4 setPosition( double x, double y, double z ) { var e = elements; e[ 12 ] = x; e[ 13 ] = y; e[ 14 ] = z; return this; }

		public Matrix4 invert()
		{
			// cofactor expansion via 2x2 sub-determinants
			var m = elements;
			double a00 = m[ 0 ], a01 = m[ 1 ], a02 = m[ 2 ], a03 = m[ 3 ];
			double a10 = m[ 4 ], a11 = m[ 5 ], a12 = m[ 6 ], a13 = m[ 7 ];
			double a20 = m[ 8 ], a21 = m[ 9 ], a22 = m[ 10 ], a23 = m[ 11 ];
			double a30 = m[ 12 ], a31 = m[ 13 ], a32 = m[ 14 ], a33 = m[ 15 ];
			double b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10, b02 = a00 * a13 - a03 * a10;
			double b03 = a01 * a12 - a02 * a11, b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12;
			double b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30, b08 = a20 * a33 - a23 * a30;
			double b09 = a21 * a32 - a22 * a31, b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32;
			double det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
			if ( det == 0 ) return set( 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 );
			double id = 1 / det;
			m[ 0 ] = ( a11 * b11 - a12 * b10 + a13 * b09 ) * id;
			m[ 1 ] = ( a02 * b10 - a01 * b11 - a03 * b09 ) * id;
			m[ 2 ] = ( a31 * b05 - a32 * b04 + a33 * b03 ) * id;
			m[ 3 ] = ( a22 * b04 - a21 * b05 - a23 * b03 ) * id;
			m[ 4 ] = ( a12 * b08 - a10 * b11 - a13 * b07 ) * id;
			m[ 5 ] = ( a00 * b11 - a02 * b08 + a03 * b07 ) * id;
			m[ 6 ] = ( a32 * b02 - a30 * b05 - a33 * b01 ) * id;
			m[ 7 ] = ( a20 * b05 - a22 * b02 + a23 * b01 ) * id;
			m[ 8 ] = ( a10 * b10 - a11 * b08 + a13 * b06 ) * id;
			m[ 9 ] = ( a01 * b08 - a00 * b10 - a03 * b06 ) * id;
			m[ 10 ] = ( a30 * b04 - a31 * b02 + a33 * b00 ) * id;
			m[ 11 ] = ( a21 * b02 - a20 * b04 - a23 * b00 ) * id;
			m[ 12 ] = ( a11 * b07 - a10 * b09 - a12 * b06 ) * id;
			m[ 13 ] = ( a00 * b09 - a01 * b07 + a02 * b06 ) * id;
			m[ 14 ] = ( a31 * b01 - a30 * b03 - a32 * b00 ) * id;
			m[ 15 ] = ( a20 * b03 - a21 * b01 + a22 * b00 ) * id;
			return this;
		}

		public Matrix4 scale( Vector3 v )
		{
			var e = elements;
			e[ 0 ] *= v.x; e[ 4 ] *= v.y; e[ 8 ] *= v.z;
			e[ 1 ] *= v.x; e[ 5 ] *= v.y; e[ 9 ] *= v.z;
			e[ 2 ] *= v.x; e[ 6 ] *= v.y; e[ 10 ] *= v.z;
			e[ 3 ] *= v.x; e[ 7 ] *= v.y; e[ 11 ] *= v.z;
			return this;
		}

		public double getMaxScaleOnAxis()
		{
			var e = elements;
			double x = e[ 0 ] * e[ 0 ] + e[ 1 ] * e[ 1 ] + e[ 2 ] * e[ 2 ];
			double y = e[ 4 ] * e[ 4 ] + e[ 5 ] * e[ 5 ] + e[ 6 ] * e[ 6 ];
			double z = e[ 8 ] * e[ 8 ] + e[ 9 ] * e[ 9 ] + e[ 10 ] * e[ 10 ];
			return Math.Sqrt( Math.Max( x, Math.Max( y, z ) ) );
		}

		public Matrix4 makeTranslation( double x, double y, double z ) => set( 1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1 );
		public Matrix4 makeTranslation( Vector3 v ) => makeTranslation( v.x, v.y, v.z );
		public Matrix4 makeRotationX( double t ) { double c = Math.Cos( t ), s = Math.Sin( t ); return set( 1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1 ); }
		public Matrix4 makeRotationY( double t ) { double c = Math.Cos( t ), s = Math.Sin( t ); return set( c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1 ); }
		public Matrix4 makeRotationZ( double t ) { double c = Math.Cos( t ), s = Math.Sin( t ); return set( c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 ); }

		public Matrix4 makeRotationAxis( Vector3 axis, double angle )
		{
			double c = Math.Cos( angle ), s = Math.Sin( angle ), t = 1 - c;
			double x = axis.x, y = axis.y, z = axis.z, tx = t * x, ty = t * y;
			return set(
				tx * x + c, tx * y - s * z, tx * z + s * y, 0,
				tx * y + s * z, ty * y + c, ty * z - s * x, 0,
				tx * z - s * y, ty * z + s * x, t * z * z + c, 0,
				0, 0, 0, 1 );
		}

		public Matrix4 makeScale( double x, double y, double z ) => set( x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1 );

		public Matrix4 compose( Vector3 position, Quaternion quaternion, Vector3 scale )
		{
			var e = elements;
			double x = quaternion.x, y = quaternion.y, z = quaternion.z, w = quaternion.w;
			double x2 = x + x, y2 = y + y, z2 = z + z;
			double xx = x * x2, xy = x * y2, xz = x * z2, yy = y * y2, yz = y * z2, zz = z * z2;
			double wx = w * x2, wy = w * y2, wz = w * z2;
			double sx = scale.x, sy = scale.y, sz = scale.z;
			e[ 0 ] = ( 1 - ( yy + zz ) ) * sx; e[ 1 ] = ( xy + wz ) * sx; e[ 2 ] = ( xz - wy ) * sx; e[ 3 ] = 0;
			e[ 4 ] = ( xy - wz ) * sy; e[ 5 ] = ( 1 - ( xx + zz ) ) * sy; e[ 6 ] = ( yz + wx ) * sy; e[ 7 ] = 0;
			e[ 8 ] = ( xz + wy ) * sz; e[ 9 ] = ( yz - wx ) * sz; e[ 10 ] = ( 1 - ( xx + yy ) ) * sz; e[ 11 ] = 0;
			e[ 12 ] = position.x; e[ 13 ] = position.y; e[ 14 ] = position.z; e[ 15 ] = 1;
			return this;
		}

		static readonly Matrix4 _m1 = new Matrix4();

		public Matrix4 decompose( Vector3 position, Quaternion quaternion, Vector3 scale )
		{
			var e = elements;
			double sx = JS.Hypot( e[ 0 ], e[ 1 ], e[ 2 ] );
			double sy = JS.Hypot( e[ 4 ], e[ 5 ], e[ 6 ] );
			double sz = JS.Hypot( e[ 8 ], e[ 9 ], e[ 10 ] );
			if ( determinant() < 0 ) sx = -sx;
			position.x = e[ 12 ]; position.y = e[ 13 ]; position.z = e[ 14 ];
			var m = _m1.copy( this ); var me = m.elements;
			double ix = 1 / sx, iy = 1 / sy, iz = 1 / sz;
			me[ 0 ] *= ix; me[ 1 ] *= ix; me[ 2 ] *= ix;
			me[ 4 ] *= iy; me[ 5 ] *= iy; me[ 6 ] *= iy;
			me[ 8 ] *= iz; me[ 9 ] *= iz; me[ 10 ] *= iz;
			quaternion.setFromRotationMatrix( m );
			scale.x = sx; scale.y = sy; scale.z = sz;
			return this;
		}

		public UnityEngine.Matrix4x4 ToUnity()
		{
			var e = elements; var m = new UnityEngine.Matrix4x4();
			for ( int c = 0; c < 4; c ++ ) for ( int r = 0; r < 4; r ++ ) m[ r, c ] = ( float ) e[ c * 4 + r ];
			return m;
		}
	}

	public sealed class Quaternion
	{
		double _x, _y, _z, _w;
		public Action onChange;

		public Quaternion( double x = 0, double y = 0, double z = 0, double w = 1 ) { _x = x; _y = y; _z = z; _w = w; }
		void Changed() { if ( onChange != null ) onChange(); }

		public double x { get => _x; set { _x = value; Changed(); } }
		public double y { get => _y; set { _y = value; Changed(); } }
		public double z { get => _z; set { _z = value; Changed(); } }
		public double w { get => _w; set { _w = value; Changed(); } }

		public Quaternion set( double x, double y, double z, double w ) { _x = x; _y = y; _z = z; _w = w; Changed(); return this; }
		public Quaternion clone() => new Quaternion( _x, _y, _z, _w );
		public Quaternion copy( Quaternion q ) { _x = q.x; _y = q.y; _z = q.z; _w = q.w; Changed(); return this; }
		public Quaternion identity() => set( 0, 0, 0, 1 );

		public Quaternion setFromEuler( Euler e, bool update = true )
		{
			double x = e.x, y = e.y, z = e.z; string order = e.order;
			double c1 = Math.Cos( x / 2 ), c2 = Math.Cos( y / 2 ), c3 = Math.Cos( z / 2 );
			double s1 = Math.Sin( x / 2 ), s2 = Math.Sin( y / 2 ), s3 = Math.Sin( z / 2 );
			switch ( order )
			{
				case "XYZ":
					_x = s1 * c2 * c3 + c1 * s2 * s3; _y = c1 * s2 * c3 - s1 * c2 * s3;
					_z = c1 * c2 * s3 + s1 * s2 * c3; _w = c1 * c2 * c3 - s1 * s2 * s3; break;
				case "YXZ":
					_x = s1 * c2 * c3 + c1 * s2 * s3; _y = c1 * s2 * c3 - s1 * c2 * s3;
					_z = c1 * c2 * s3 - s1 * s2 * c3; _w = c1 * c2 * c3 + s1 * s2 * s3; break;
				case "ZXY":
					_x = s1 * c2 * c3 - c1 * s2 * s3; _y = c1 * s2 * c3 + s1 * c2 * s3;
					_z = c1 * c2 * s3 + s1 * s2 * c3; _w = c1 * c2 * c3 - s1 * s2 * s3; break;
				case "ZYX":
					_x = s1 * c2 * c3 - c1 * s2 * s3; _y = c1 * s2 * c3 + s1 * c2 * s3;
					_z = c1 * c2 * s3 - s1 * s2 * c3; _w = c1 * c2 * c3 + s1 * s2 * s3; break;
				case "YZX":
					_x = s1 * c2 * c3 + c1 * s2 * s3; _y = c1 * s2 * c3 + s1 * c2 * s3;
					_z = c1 * c2 * s3 - s1 * s2 * c3; _w = c1 * c2 * c3 - s1 * s2 * s3; break;
				case "XZY":
					_x = s1 * c2 * c3 - c1 * s2 * s3; _y = c1 * s2 * c3 - s1 * c2 * s3;
					_z = c1 * c2 * s3 + s1 * s2 * c3; _w = c1 * c2 * c3 + s1 * s2 * s3; break;
				default: throw new ArgumentException( "Quaternion.setFromEuler: unknown order " + order );
			}

			if ( update ) Changed();
			return this;
		}

		public Quaternion setFromAxisAngle( Vector3 axis, double angle )
		{
			double h = angle / 2, s = Math.Sin( h );
			_x = axis.x * s; _y = axis.y * s; _z = axis.z * s; _w = Math.Cos( h );
			Changed();
			return this;
		}

		public Quaternion setFromRotationMatrix( Matrix4 m )
		{
			var e = m.elements;
			double m11 = e[ 0 ], m12 = e[ 4 ], m13 = e[ 8 ], m21 = e[ 1 ], m22 = e[ 5 ], m23 = e[ 9 ], m31 = e[ 2 ], m32 = e[ 6 ], m33 = e[ 10 ];
			double tr = m11 + m22 + m33;
			if ( tr > 0 )
			{
				double s = 0.5 / Math.Sqrt( tr + 1 );
				_w = 0.25 / s; _x = ( m32 - m23 ) * s; _y = ( m13 - m31 ) * s; _z = ( m21 - m12 ) * s;
			}
			else if ( m11 > m22 && m11 > m33 )
			{
				double s = 2 * Math.Sqrt( 1 + m11 - m22 - m33 );
				_w = ( m32 - m23 ) / s; _x = 0.25 * s; _y = ( m12 + m21 ) / s; _z = ( m13 + m31 ) / s;
			}
			else if ( m22 > m33 )
			{
				double s = 2 * Math.Sqrt( 1 + m22 - m11 - m33 );
				_w = ( m13 - m31 ) / s; _x = ( m12 + m21 ) / s; _y = 0.25 * s; _z = ( m23 + m32 ) / s;
			}
			else
			{
				double s = 2 * Math.Sqrt( 1 + m33 - m11 - m22 );
				_w = ( m21 - m12 ) / s; _x = ( m13 + m31 ) / s; _y = ( m23 + m32 ) / s; _z = 0.25 * s;
			}

			Changed();
			return this;
		}

		public Quaternion setFromUnitVectors( Vector3 from, Vector3 to )
		{
			double r = from.x * to.x + from.y * to.y + from.z * to.z + 1;
			if ( r < JS.EPSILON )
			{
				// opposite vectors: rotate 180 degrees around any orthogonal axis
				r = 0;
				if ( Math.Abs( from.x ) > Math.Abs( from.z ) ) { _x = -from.y; _y = from.x; _z = 0; _w = r; }
				else { _x = 0; _y = -from.z; _z = from.y; _w = r; }
			}
			else
			{
				_x = from.y * to.z - from.z * to.y;
				_y = from.z * to.x - from.x * to.z;
				_z = from.x * to.y - from.y * to.x;
				_w = r;
			}

			return normalize();
		}

		public double angleTo( Quaternion q ) => 2 * Math.Acos( Math.Abs( Math.Max( -1, Math.Min( 1, dot( q ) ) ) ) );
		public Quaternion rotateTowards( Quaternion q, double step ) { double angle = angleTo( q ); if ( angle == 0 ) return this; return slerp( q, Math.Min( 1, step / angle ) ); }
		public Quaternion invert() => conjugate();
		public Quaternion conjugate() { _x *= -1; _y *= -1; _z *= -1; Changed(); return this; }
		public double dot( Quaternion q ) => _x * q._x + _y * q._y + _z * q._z + _w * q._w;
		public double lengthSq() => _x * _x + _y * _y + _z * _z + _w * _w;
		public double length() => Math.Sqrt( lengthSq() );

		public Quaternion normalize()
		{
			double l = length();
			if ( l == 0 ) { _x = 0; _y = 0; _z = 0; _w = 1; }
			else { l = 1 / l; _x *= l; _y *= l; _z *= l; _w *= l; }
			Changed();
			return this;
		}

		public Quaternion multiply( Quaternion q ) => multiplyQuaternions( this, q );
		public Quaternion premultiply( Quaternion q ) => multiplyQuaternions( q, this );

		public Quaternion multiplyQuaternions( Quaternion a, Quaternion b )
		{
			double ax = a._x, ay = a._y, az = a._z, aw = a._w, bx = b._x, by = b._y, bz = b._z, bw = b._w;
			_x = ax * bw + aw * bx + ay * bz - az * by;
			_y = ay * bw + aw * by + az * bx - ax * bz;
			_z = az * bw + aw * bz + ax * by - ay * bx;
			_w = aw * bw - ax * bx - ay * by - az * bz;
			Changed();
			return this;
		}

		public Quaternion slerp( Quaternion qb, double t )
		{
			if ( t == 0 ) return this;
			if ( t == 1 ) return copy( qb );
			double x = _x, y = _y, z = _z, w = _w;
			double cosHalf = w * qb._w + x * qb._x + y * qb._y + z * qb._z;
			double bx = qb._x, by = qb._y, bz = qb._z, bw = qb._w;
			if ( cosHalf < 0 ) { bx = -bx; by = -by; bz = -bz; bw = -bw; cosHalf = -cosHalf; }
			if ( cosHalf >= 1 ) return this;
			double sqrSin = 1 - cosHalf * cosHalf;
			if ( sqrSin <= JS.EPSILON )
			{
				double s = 1 - t;
				_w = s * w + t * bw; _x = s * x + t * bx; _y = s * y + t * by; _z = s * z + t * bz;
				return normalize();
			}

			double sinHalf = Math.Sqrt( sqrSin ), half = Math.Atan2( sinHalf, cosHalf );
			double ra = Math.Sin( ( 1 - t ) * half ) / sinHalf, rb = Math.Sin( t * half ) / sinHalf;
			_w = w * ra + bw * rb; _x = x * ra + bx * rb; _y = y * ra + by * rb; _z = z * ra + bz * rb;
			Changed();
			return this;
		}

		public UnityEngine.Quaternion ToUnity() => new UnityEngine.Quaternion( ( float ) _x, ( float ) _y, ( float ) _z, ( float ) _w );
	}

	public sealed class Euler
	{
		double _x, _y, _z; string _order;
		public Action onChange;
		public const string DEFAULT_ORDER = "XYZ";

		public Euler( double x = 0, double y = 0, double z = 0, string order = DEFAULT_ORDER ) { _x = x; _y = y; _z = z; _order = order; }
		void Changed() { if ( onChange != null ) onChange(); }

		public double x { get => _x; set { _x = value; Changed(); } }
		public double y { get => _y; set { _y = value; Changed(); } }
		public double z { get => _z; set { _z = value; Changed(); } }
		public string order { get => _order; set { _order = value; Changed(); } }

		public Euler set( double x, double y, double z, string order = null ) { _x = x; _y = y; _z = z; if ( order != null ) _order = order; Changed(); return this; }
		public Euler clone() => new Euler( _x, _y, _z, _order );
		public Euler copy( Euler e ) => set( e._x, e._y, e._z, e._order );

		static double Clamp1( double v ) => Math.Max( -1, Math.Min( 1, v ) );
		static readonly Matrix4 _m = new Matrix4();

		// `m` must be a pure rotation (unscaled) in its upper 3x3.
		public Euler setFromRotationMatrix( Matrix4 m, string order = null, bool update = true )
		{
			order = order ?? _order;
			var e = m.elements;
			double m11 = e[ 0 ], m12 = e[ 4 ], m13 = e[ 8 ], m21 = e[ 1 ], m22 = e[ 5 ], m23 = e[ 9 ], m31 = e[ 2 ], m32 = e[ 6 ], m33 = e[ 10 ];
			double lim = 0.9999999;
			switch ( order )
			{
				case "XYZ":
					_y = Math.Asin( Clamp1( m13 ) );
					if ( Math.Abs( m13 ) < lim ) { _x = Math.Atan2( -m23, m33 ); _z = Math.Atan2( -m12, m11 ); } else { _x = Math.Atan2( m32, m22 ); _z = 0; }
					break;
				case "YXZ":
					_x = Math.Asin( -Clamp1( m23 ) );
					if ( Math.Abs( m23 ) < lim ) { _y = Math.Atan2( m13, m33 ); _z = Math.Atan2( m21, m22 ); } else { _y = Math.Atan2( -m31, m11 ); _z = 0; }
					break;
				case "ZXY":
					_x = Math.Asin( Clamp1( m32 ) );
					if ( Math.Abs( m32 ) < lim ) { _y = Math.Atan2( -m31, m33 ); _z = Math.Atan2( -m12, m22 ); } else { _y = 0; _z = Math.Atan2( m21, m11 ); }
					break;
				case "ZYX":
					_y = Math.Asin( -Clamp1( m31 ) );
					if ( Math.Abs( m31 ) < lim ) { _x = Math.Atan2( m32, m33 ); _z = Math.Atan2( m21, m11 ); } else { _x = 0; _z = Math.Atan2( -m12, m22 ); }
					break;
				case "YZX":
					_z = Math.Asin( Clamp1( m21 ) );
					if ( Math.Abs( m21 ) < lim ) { _x = Math.Atan2( -m23, m22 ); _y = Math.Atan2( -m31, m11 ); } else { _x = 0; _y = Math.Atan2( m13, m33 ); }
					break;
				case "XZY":
					_z = Math.Asin( -Clamp1( m12 ) );
					if ( Math.Abs( m12 ) < lim ) { _x = Math.Atan2( m32, m22 ); _y = Math.Atan2( m13, m11 ); } else { _x = Math.Atan2( -m23, m33 ); _y = 0; }
					break;
				default: throw new ArgumentException( "Euler.setFromRotationMatrix: unknown order " + order );
			}

			_order = order;
			if ( update ) Changed();
			return this;
		}

		public Euler setFromQuaternion( Quaternion q, string order = null, bool update = true )
		{
			_m.makeRotationFromQuaternion( q );
			return setFromRotationMatrix( _m, order, update );
		}
	}
}
