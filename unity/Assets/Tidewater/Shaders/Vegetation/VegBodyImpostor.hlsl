// The impostor surface ( Impostors.js createMaterial ): the view ray of the pixel is re-projected on the plane of the three baked frames around the view direction.
// Declares `s` and `Ns`.
	float3 geoN = vegM( normalize( input.tangentToWorld[ 2 ] ) );
	bool front = input.isFrontFace;
	float3 Ns = front ? geoN : - geoN;
	float3 Ps = vegM( GetAbsolutePositionWS( input.positionRWS ) );
	float3 Vs = vegM( V );
	uint ri = ( uint )( input.texCoord2.x + 0.5 );
	float4 iPos = _VegInst[ ri * 3u ];
	float4 iDat = _VegInst[ ri * 3u + 1u ];
	bool g1Flag = iDat.y < 0.0;
	float R = g1Flag ? _VegGroup1.y : _VegGroup0.y;
	float Cy = g1Flag ? _VegGroup1.x : _VegGroup0.x;
	float vBase = g1Flag ? _VegImpInfo.x : 0.0;
	float3 base = iPos.xyz;
	float si = input.color.w;
	float sy = abs( iDat.y );
	float yaw = iDat.x;
	float cyw = cos( yaw ); float syw = sin( yaw );
	float3 C = base + float3( 0.0, Cy * si * sy, 0.0 ) + input.color.xyz;
	// world -> plant-local ( unstretched, centred ): rotate by -yaw, divide by the scale
	float3 Ow = _VegCam.xyz - C;
	float3 Dw = Ps - _VegCam.xyz;
	float3 scl = float3( si, si * sy, si );
	float3 O = float3( Ow.x * cyw - Ow.z * syw, Ow.y, Ow.x * syw + Ow.z * cyw ) / scl;
	float3 D = float3( Dw.x * cyw - Dw.z * syw, Dw.y, Dw.x * syw + Dw.z * cyw ) / scl;
	float3 vdir = normalize( O );
	float3 vd = normalize( float3( vdir.x, max( vdir.y, 0.02 ), vdir.z ) );
	float2 gg = ( vegOctEncode( vd ) * 0.5 + 0.5 ) * ( VEG_OCT_N - 1.0 );
	float2 gi = floor( clamp( gg, 0.0, VEG_OCT_N - 1.001 ) );
	float2 fr = gg - gi;
	bool upper = fr.x + fr.y > 1.0;
	// triangle of the cell containing g, barycentric weights
	float2 i0 = upper ? gi + 1.0 : gi;
	float2 i1 = upper ? gi + float2( 0.0, 1.0 ) : gi + float2( 1.0, 0.0 );
	float2 i2 = upper ? gi + float2( 1.0, 0.0 ) : gi + float2( 0.0, 1.0 );
	float w0 = upper ? fr.x + fr.y - 1.0 : 1.0 - fr.x - fr.y;
	float w1 = upper ? 1.0 - fr.x : fr.x;
	float w2 = upper ? 1.0 - fr.y : fr.y;
	float variant = vBase + vegVariantOf( iDat.w, g1Flag );
	float4 vA = 0.0; float4 vB = 0.0;
	// near: blend the three frames around the view direction; far: the dominant frame only
	bool blend = length( _VegCam.xyz - base ) < VEG_IMP_BLEND_DIST;
	if ( blend )
	{
		float4 a0, b0, a1, b1, a2, b2;
		vegImpSample( i0, O, D, variant, R, a0, b0 );
		vegImpSample( i1, O, D, variant, R, a1, b1 );
		vegImpSample( i2, O, D, variant, R, a2, b2 );
		vA = a0 * w0 + a1 * w1 + a2 * w2;
		vB = b0 * w0 + b1 * w1 + b2 * w2;
	}
	else
	{
		float2 iMax = w0 >= max( w1, w2 ) ? i0 : ( w1 >= w2 ? i1 : i2 );
		vegImpSample( iMax, O, D, variant, R, vA, vB );
	}

	// cross-fade from the near geometry ( incoming level of the band around the hand-over distance )
	float nd = g1Flag ? _VegNear.y : _VegNear.x;
	float fade = vsm( nd * ( 1.0 - VEG_LOD_BAND / 2.0 ), nd * ( 1.0 + VEG_LOD_BAND / 2.0 ), length( _VegCam.xyz - base ) );
	if ( ! ( vA.w > 0.42 && vegBayer4( input.positionSS.xy ) < fade ) ) discard;

	float cov = max( vA.w, 1e-3 );
	float bright = vA.x / cov; float leaf = vA.y / cov; float cr = vA.z / cov;
	// exposure ( baked crown AO ): the inside and underside of the crown in deep shade
	float ex = vB.w / cov;
	float3 albedoI = vegImpostorColor( iDat.w, cr, leaf, bright, g1Flag ) * lerp( 0.26, 1.0, ex * ex ) * ( bright * 0.6 + 0.7 );
	// B is written where A has coverage: un-premultiply the filtered edges by A's coverage
	float3 nl = vB.xyz / max( vA.w, 1e-3 ) * 2.0 - 1.0;
	// local -> world: inverse-transpose of the stretch, then the yaw rotation
	float3 ns = float3( nl.x, nl.y / sy, nl.z );
	float3 nw = normalize( float3( ns.x * cyw + ns.z * syw, ns.y, ns.z * cyw - ns.x * syw ) );
	VegOut s;
	s.albedo = albedoI;
	s.normal = normalize( nw + Vs * 0.12 );
	s.roughness = 0.85;
	s.ao = 1.0;
