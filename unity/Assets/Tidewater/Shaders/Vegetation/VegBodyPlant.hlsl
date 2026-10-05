// The plant surface (VegMaterials.js createPlantLeafMaterial: surface + shadow mask) as the body of GetSurfaceAndBuiltinData, with `input`, `V` in scope: declares `s`
// ( .albedo .normal .roughness .ao ) and `Ns` ( the geometric normal, sim frame, flipped on back faces ). The mask discards in here.
	float3 geoN = vegM( normalize( input.tangentToWorld[ 2 ] ) );
	bool front = input.isFrontFace;
	float3 Ns = front ? geoN : - geoN;
	float3 Ps = vegM( GetAbsolutePositionWS( input.positionRWS ) );
	float3 Vs = vegM( V );
	uint ri = ( uint )( input.texCoord2.x + 0.5 );
	float4 iPos = _VegInst[ ri * 3u ];
	float4 iDat = _VegInst[ ri * 3u + 1u ];

	VegPlantIn pf;
	pf.st = input.texCoord0.xy;
	pf.aMat = input.color;
	pf.seed = iDat.w;
	pf.H = iDat.z;
	pf.base = iPos.xyz;
	pf.trunkY = input.texCoord1.x;
	pf.hGround = Ps.y - iPos.y;
	pf.pixel = input.positionSS.xy;
	pf.fwS = fwidth( pf.st.x );
	if ( ! vegPlantMask( pf ) ) discard;

	float part = pf.aMat.x;
	float age = pf.aMat.y;
	bool isStem = part < 0.5;
	bool isNut = part > 4.5 && part < 5.5;
	bool isLeaf = ! isStem && ! isNut;
	bool isBroad = part > 5.5;
	float ivN = vegHash12( float2( pf.seed * 37.1, 1.7 ) );
	VegBark bk;
	bk.bark = float3( 0.0, 0.0, 0.0 ); bk.hd = 0.0;
	if ( isStem ) bk = vegPalmBark( pf.trunkY, pf.st.x, pf.H, pf.seed, ivN );
	float3 albedoP = vegPlantAlbedo( pf, bk );
	// the underside of fronds and leaves is duller and a little bluer than the waxy upper side
	albedoP = ( front || ! isLeaf ) ? albedoP : albedoP * float3( 0.74, 0.8, 0.84 );

	// palm trunks: bark relief (rings, fissures, roots, fibres) along the trunk axis (finite difference of the bark height, faded with distance); leaves: normals bent
	// towards the viewer
	float3 Tt = normalize( vegM( input.tangentToWorld[ 0 ].xyz ) );
	float dB = 0.0;
	float dA = 0.0;
	float fadeB = 1.0 - vsm( 10.0, 32.0, length( _VegCam.xyz - Ps ) );
	if ( isStem && fadeB > 0.0 )
	{
		float e = 0.004;
		float ea = 0.0015; // around the trunk (a: 0..1 over a ~1.1 m circumference)
		float h0 = bk.hd;
		float slope = ( vegPalmBark( pf.trunkY + e, pf.st.x, pf.H, pf.seed, ivN ).hd - h0 ) / e;
		float slopeA = ( vegPalmBark( pf.trunkY, pf.st.x + ea, pf.H, pf.seed, ivN ).hd - h0 ) / ( ea * 1.1 );
		dB = slope * fadeB * ( 1.0 - age );
		dA = slopeA * fadeB * ( 1.0 - age );
	}

	// roughness: broadleaf plants glossy to satin (dead leaves dull); fronds waxy but not glossy
	float broadR = part < 6.5 ? 0.46 : ( part < 7.5 ? 0.58 : 0.42 );
	VegOut s;
	s.albedo = albedoP;
	s.normal = normalize( Ns - Tt * dB - cross( Ns, Tt ) * dA + Vs * ( isLeaf ? 0.15 : 0.0 ) );
	s.roughness = isBroad ? lerp( broadR, 0.85, vsm( 0.9, 0.98, age ) ) : ( isStem ? 0.92 : ( ( part < 1.5 || isNut ) ? 0.62 : 0.7 ) );
	s.ao = 1.0;
