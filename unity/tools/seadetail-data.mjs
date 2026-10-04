// Runs makeNoiseTexture() of src/ocean/SeaDetail.js without a GPU: the module is loaded from its own source with the engine
// import replaced by stubs, so the code under test is the original. Returns the Uint16Array of half floats.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export async function seaDetailNoise( root ) {

	let src = fs.readFileSync( path.join( root, 'src/ocean/SeaDetail.js' ), 'utf8' );
	src = src.replace( /import \{ Vector2 \} from [^\n]+\n/, 'class Vector2 { constructor() { this.x = 0; this.y = 0; } }\n' );
	src = src.replace( /import \{ toHalfFloat \} from [^\n]+\n/, `import { toHalfFloat } from '${ path.join( root, 'src/engine/math/DataUtils.js' ) }';\n` );
	src = src.replace( /import \{ Texture, UniformBlock, ShaderModule, commonModule \} from [^\n]+\n/, 'class Texture { constructor( o ) { Object.assign( this, o ); } }\nconst UniformBlock = class {}, ShaderModule = class {}, commonModule = {};\n' );
	src = src.replace( /import \{ G \} from [^\n]+\n/, 'const G = {};\n' );
	src = src.replace( /import \{ mulberry32 \} from [^\n]+\n/, `import { mulberry32 } from '${ path.join( root, 'src/util/Noise.js' ) }';\n` );
	const tmp = path.join( os.tmpdir(), 'tw-sea-detail.mjs' );
	fs.writeFileSync( tmp, src + '\nexport const __noise = makeNoiseTexture;\n' );
	const mod = await import( tmp );
	return mod.__noise( 256 ).data;

}
