// How many water-surface queries the GPU answers per frame (see WaterQuery.js). Kept apart from it so
// the headless tests can import the numbers without pulling in the WebGPU engine.

export const MAX_QUERIES = 256; // the whole table: slot 0 is the camera, the rest are named blocks
export const QUERY_WORKGROUP = 64; // threads per workgroup in the query kernel

// Workgroups to dispatch for `count` slots in use. The kernel must cover every slot: a slot past the
// last dispatched workgroup is never computed and reads back as zeros, with no error anywhere.
export function queryWorkgroups( count ) {

	return Math.ceil( Math.min( Math.max( count, 1 ), MAX_QUERIES ) / QUERY_WORKGROUP );

}
