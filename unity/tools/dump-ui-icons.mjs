// Prints the settings panel's icon set (src/ui/icons.js) as JSON: { name: '<svg ...>' }, every name in PATHS and every alias, as the JS icon() writes it.
//   node unity/tools/dump-ui-icons.mjs        (make-ui-icons.py runs it)
import fs from 'fs';
import { fileURLToPath } from 'url';
import path from 'path';
import { icon } from '../../src/ui/icons.js';

const file = path.join( path.dirname( fileURLToPath( import.meta.url ) ), '../../src/ui/icons.js' );
const src = fs.readFileSync( file, 'utf8' );
const a = src.indexOf( 'const PATHS = {' ), b = src.indexOf( 'const ALIASES' );
const names = [ ...src.slice( a, b ).matchAll( /^\t'?([a-z0-9-]+)'?:/gm ) ].map( ( m ) => m[ 1 ] );
const out = {};
for ( const n of names ) out[ n ] = icon( n ).replace( 'class="tw-ico" ', 'xmlns="http://www.w3.org/2000/svg" ' ).replace( 'aria-hidden="true" focusable="false"', '' );
process.stdout.write( JSON.stringify( out ) );
