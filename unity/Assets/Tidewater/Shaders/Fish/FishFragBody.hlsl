// The skin at this fragment, as the body of a fragment function with `input` (FragInputs) and `V` in scope: declares `FishOut s`. The stochastic fin transparency and the
// level-of-detail cross-fade discard in here, so it is included where it is used (GetSurfaceAndBuiltinData, FishStudioPass.hlsl): the discard in a function of its own
// breaks the DXC compile ("argument pulled into unrelated predicate").
	float4 D = float4( input.texCoord0.xy, input.texCoord1.xy );

#ifdef FISH_SWIM
	// the record of this instance again (the entry came in uv3.y): pattern + seed, length; the level-of-detail cross-fade of the entry
	uint e = ( uint )( input.texCoord3.y + 0.5 );
	bool fadeDraw = _FishDraw.y > 0.5;
	uint entry = fadeDraw ? _FishFadeList[ e ] : _FishList[ e ];
	uint ri = fadeDraw ? ( entry & 0xffffffu ) : entry;
	float patternSeed = _FishInstances[ ri * 4u + 2u ].w;
	float fishLength = _FishInstances[ ri * 4u ].w;
	float4 fishFlags = float4( 0.0, 0.0, 0.0, 0.0 );
	if ( fadeDraw && ! fishLodVisible( input.positionSS.xy, float( ( entry >> 24u ) & 127u ) / 127.0, ( entry >> 31u ) != 0u ) ) discard;
#else
	float patternSeed = _FishA.x;
	float fishLength = _FishC.x;
	float4 fishFlags = _FishB;
	// stochastic fin transparency (every pass) and the level-of-detail cross-fade (not in the shadow proxy)
	if ( ! fishPropKeep( D, input.positionSS.xy, _TWFrame.x ) ) discard;
#if SHADERPASS != SHADERPASS_SHADOWS
	if ( ! fishLodVisible( input.positionSS.xy, _FishC.y, _FishC.z > 0.5 ) ) discard;
#endif
#endif

	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );

	FishIn fi;
	fi.D = D;
	fi.Lp = float3( input.texCoord2.xy, input.texCoord3.x );
	fi.I = float4( floor( patternSeed ), frac( patternSeed ), fishLength, 0.0 );
	fi.flags = fishFlags;
	fi.P = input.positionRWS;
	fi.N = normalWS;
	fi.V = V;
	fi.Pview = mul( ( float3x3 ) UNITY_MATRIX_V, input.positionRWS );

	FishOut s = FishSurface( fi );

