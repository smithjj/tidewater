// The canopy surface (VegMaterials.js createCanopyMaterial: surface + shadow mask) as the body of GetSurfaceAndBuiltinData: declares `s` and `Ns`. The mask discards in here.
	float3 geoN = vegM( normalize( input.tangentToWorld[ 2 ] ) );
	bool front = input.isFrontFace;
	float3 Ns = front ? geoN : - geoN;
	float3 Ps = vegM( GetAbsolutePositionWS( input.positionRWS ) );
	float3 Vs = vegM( V );
	uint ri = ( uint )( input.texCoord2.x + 0.5 );
	float4 iPos = _VegInst[ ri * 3u ];
	float4 iDat = _VegInst[ ri * 3u + 1u ];
	float4 aMat = input.color;
	float part = aMat.x; float ao = aMat.y; float cr = aMat.z;
	float seed = iDat.w;
	bool isBark = part < 0.5 || part > 4.5;
	bool isShrub = part > 2.5;
	float spT = vegTreeSpecies( seed ); float spS = vegShrubSpecies( seed );
	// leaf cluster sampled once ( first in the fragment shader ), shared by the mask and the colour
	float2 st = input.texCoord0.xy;
	float4 L = vegLeafSample( st, vegLeafTile( seed, part > 2.5 ) );
	{
		// cross-fade into the impostors ( outgoing level of the band around the hand-over distance )
		float nearD = iDat.y < 0.0 ? _VegNear.y : _VegNear.x;
		float fade = vsm( nearD * ( 1.0 - VEG_LOD_BAND / 2.0 ), nearD * ( 1.0 + VEG_LOD_BAND / 2.0 ), length( _VegCam.xyz - iPos.xyz ) );
		// cards seen edge-on thin out ( no sliver lines through the crown )
		float facing = abs( dot( normalize( cross( ddx( Ps ), ddy( Ps ) ) ), normalize( Vs ) ) );
		float thr = vegCoverageThreshold( st ) + ( 1.0 - vsm( 0.08, 0.35, facing ) ) * 0.45;
		if ( ! ( ( isBark || L.x > thr ) && vegBayer4( input.positionSS.xy ) >= fade ) ) discard;
	}

	float bright = L.y * 1.4;
	float cell = L.z;
	float3 c = vegCanopyLeafColor( seed, cr, isShrub ) * bright;
	// species details: red-veined old leaves ( sea grape ), variegation ( croton ), flowers ( hibiscus )
	bool shrub0 = isShrub && spS < 0.5; bool shrub1 = isShrub && spS == 1.0; bool shrub2 = isShrub && spS > 1.5;
	c = lerp( c, vegC( 0x7a3a22u ), vsm( 0.86, 0.98, cell ) * 0.55 * ( shrub0 ? 1.0 : 0.0 ) );
	float3 vari = frac( cell * 7.3 ) > 0.5 ? vegC( 0x9a8a30u ) : vegC( 0x7a3a22u );
	c = lerp( c, vari, vsm( 0.72, 0.9, cell ) * 0.55 * ( shrub1 ? 1.0 : 0.0 ) );
	c = ( shrub2 && cell > 0.92 ) ? vegC( 0xb3261eu ) : c;
	// trees: a few old leaves turning red / yellow before they drop ( sea almond )
	float treeK = isShrub ? 0.0 : 1.0;
	c = lerp( c, lerp( vegC( 0x8a7a3au ), vegC( 0x7e3e22u ), step( 0.992, frac( cell * 3.7 ) ) ), step( 0.984, frac( cell * 3.7 ) ) * treeK * 0.6 );
	// sunlit outer / upper leaves brighter and a little yellow-green; shaded interior kept for contrast
	float outer = vsm( 0.62, 1.0, ao );
	c = lerp( c, c * float3( 1.16, 1.22, 0.92 ), outer * 0.7 );
	float3 leaf = c * lerp( 0.55, 1.0, ao );
	// bark: grey-brown with vertical streaks, lichen patches
	float hfB = input.texCoord1.y; // ( the vertex stage passes aVeg.x on )
	float3 albedoC = leaf;
	if ( isBark )
	{
		float n2 = vegNoise( float2( st.x * 6.0, st.y * 0.7 ) );
		float3 bark = vegBarkColor( vegNoise( float2( st.x * 30.0, st.y * 2.5 + seed * 40.0 ) ), n2 );
		// moss and epiphytes on the humid lower trunk and the upper sides of the limbs
		bark = lerp( bark, lerp( vegC( 0x2c3a18u ), vegC( 0x44552au ), n2 ), vsm( 0.45, 0.7, vegNoise( float2( st.x * 9.0, st.y * 1.3 + seed * 11.0 ) ) + ( 0.35 - hfB ) * 0.6 ) * 0.7 );
		albedoC = bark;
	}

	VegOut s;
	s.albedo = albedoC;
	// bark: the trunk under the crown sees little of the sky
	s.ao = isBark ? vsm( 0.0, 0.75, hfB ) * 0.45 + 0.4 : lerp( 0.35, 1.0, ao );
	s.roughness = isBark ? 0.92 : ( ( ( isShrub && spS < 0.5 ) || ( ! isShrub && spT < 0.5 ) ) ? 0.65 : 0.82 );
	// leaves: canopy ( spherical ) normals, not flipped on back faces, bent towards the viewer so they are never shaded at grazing angles ( avoids a white Fresnel sheen
	// when backlit )
	float3 geoLeaf = normalize( vegM( normalize( input.tangentToWorld[ 2 ] ) ) );
	s.normal = isBark ? Ns : normalize( geoLeaf + Vs * 0.7 );
