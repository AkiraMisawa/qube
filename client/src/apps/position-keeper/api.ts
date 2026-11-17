import { httpApi, outputOf, type Api } from '../../platform/api';
import type { PkCreated, PkPosition, PkView, TableRead } from './contracts';

// The Position Keeper page's reads (ADR-0019): commands through the page's door, as everything else it does. There is no
// endpoint of the Position Keeper's own.

const VERSION = '1.0.0';
export const HEDGES_TABLE = 'pk.hedges';
export const MOVES_TABLE = 'pk.moves';
export const HEDGE_FIELDS = ['product', 'currency', 'direction', 'notional', 'end', 'rate'] as const;
export const MOVE_FIELDS = ['kind', 'input', 'typedIn', 'move'] as const;

export interface PkApi {
  /** Makes a workspace on a result set: the one named, or the one the host is configured with. */
  create(resultSet: string | undefined): Promise<PkCreated>;
  /** The workspace as its page shows it, every part read at one revision. */
  view(workspaceId: string): Promise<PkView>;
}

export function pkApi(api: Pick<Api, 'command'>): PkApi {
  const ask = async <T>(capability: string, request: Readonly<Record<string, unknown>>, port: string): Promise<T> =>
    outputOf<T>(await api.command(capability, VERSION, { request }, crypto.randomUUID()), port);
  const table = (workspaceId: string, revision: number, name: string, fields: readonly string[]) =>
    ask<TableRead>('tables.read', { workspaceId, revision, table: name, rows: 'all', fields }, 'answer');
  return {
    create: (resultSet) => ask<PkCreated>('pk.workspace.create', resultSet === undefined ? {} : { resultSet }, 'workspace'),
    view: async (workspaceId) => {
      const position = await ask<PkPosition>('pk.position.read', { workspaceId }, 'answer');
      // The tables are read at the position's revision: a workspace that moved between the reads refuses them, and the
      // page reads again, so no part of what it shows is of another revision.
      const [hedges, moves] = await Promise.all([
        table(workspaceId, position.revision, HEDGES_TABLE, HEDGE_FIELDS),
        table(workspaceId, position.revision, MOVES_TABLE, MOVE_FIELDS),
      ]);
      for (const read of [hedges, moves])
        if (read.more > 0) throw new Error(`The server left ${read.more} rows out of a table the Position Keeper page shows whole.`);
      return { position, hedges, moves };
    },
  };
}

export const httpPkApi: PkApi = pkApi(httpApi);
