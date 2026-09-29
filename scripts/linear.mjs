#!/usr/bin/env node
// linear.mjs — minimal Linear helper (no dependencies).
// Reads LINEAR_API_KEY from env or <repoRoot>/.env.local.
//
// Usage:
//   node scripts/linear.mjs create --project <name|id> --title <t> [--desc <d>] [--state <name>]
//   node scripts/linear.mjs comment <issueId> <body...>
//   node scripts/linear.mjs state <issueId> <stateName>
//   node scripts/linear.mjs find --project <name|id> --title <prefix>
//   node scripts/linear.mjs find-key <ISSUE-KEY>
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const API = 'https://api.linear.app/graphql';

function loadEnv() {
  const f = join(ROOT, '.env.local');
  if (!existsSync(f)) return;
  for (const line of readFileSync(f, 'utf8').split('\n')) {
    const m = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/);
    if (!m || m[1].startsWith('#')) continue;
    if (!(m[1] in process.env)) process.env[m[1]] = m[2].replace(/^["']|["']$/g, '');
  }
}

async function gql(query, variables = {}) {
  const key = process.env['LINEAR_API_KEY'];
  if (!key) {
    console.error('LINEAR_API_KEY missing (env or .env.local)');
    process.exit(1);
  }
  let lastErr = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      const res = await fetch(API, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: key },
        body: JSON.stringify({ query, variables }),
      });
      const text = await res.text();
      const json = JSON.parse(text);
      if (json.errors?.length) {
        console.error('Linear API error:', JSON.stringify(json.errors, null, 2));
        process.exit(1);
      }
      return json.data;
    } catch (e) {
      lastErr = e;
      if (attempt < 3) await new Promise((r) => setTimeout(r, 2000 * attempt));
    }
  }
  console.error('Linear API failed after 3 attempts:', lastErr?.message ?? lastErr);
  process.exit(1);
}

function parseArgs(argv) {
  const flags = {};
  const pos = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const eq = a.indexOf('=');
      if (eq > -1) flags[a.slice(2, eq)] = a.slice(eq + 1);
      else flags[a.slice(2)] = argv[++i] ?? '';
    } else pos.push(a);
  }
  return { flags, pos };
}

async function resolveProject(ref) {
  const d = await gql(`{ projects(first: 100) { nodes { id name } } }`);
  const p = d.projects.nodes.find(
    (p) => p.id === ref || p.name.toLowerCase() === String(ref).toLowerCase(),
  );
  if (!p) {
    console.error(`Project not found: ${ref}`);
    process.exit(1);
  }
  return p;
}

async function resolveTeamOfProject(projectId) {
  const d = await gql(`query($id: String!) { project(id: $id) { teams(first: 1) { nodes { id } } } }`, {
    id: projectId,
  });
  return d.project.teams.nodes[0]?.id;
}

async function resolveState(teamId, name) {
  const d = await gql(
    `query($t: WorkflowStateFilter!) { workflowStates(filter: $t, first: 50) { nodes { id name } } }`,
    { t: { team: { id: { eq: teamId } } } },
  );
  const s = d.workflowStates.nodes.find((s) => s.name.toLowerCase() === name.toLowerCase());
  if (!s) {
    console.error(
      `State not found: ${name} (available: ${d.workflowStates.nodes.map((s) => s.name).join(', ')})`,
    );
    process.exit(1);
  }
  return s;
}

const [cmd, ...rest] = process.argv.slice(2);
const { flags, pos } = parseArgs(rest);
loadEnv();

switch (cmd) {
  case 'create': {
    if (!flags['project'] || !flags['title']) {
      console.error('Usage: create --project <name|id> --title <t> [--desc <d>] [--state <name>]');
      process.exit(1);
    }
    const p = await resolveProject(flags['project']);
    const teamId = await resolveTeamOfProject(p.id);
    const input = { teamId, projectId: p.id, title: flags['title'], description: flags['desc'] ?? '' };
    if (flags['state']) input.stateId = (await resolveState(teamId, flags['state'])).id;
    const d = await gql(
      `mutation($i: IssueCreateInput!) { issueCreate(input: $i) { success issue { id identifier title url } } }`,
      { i: input },
    );
    const iss = d.issueCreate.issue;
    console.log(`${iss.identifier}\t${iss.title}\n${iss.url}\nid: ${iss.id}`);
    break;
  }
  case 'comment': {
    const [id, ...body] = pos;
    if (!id || !body.length) {
      console.error('Usage: comment <issueId> <body...>');
      process.exit(1);
    }
    await gql(`mutation($i: CommentCreateInput!) { commentCreate(input: $i) { success } }`, {
      i: { issueId: id, body: body.join(' ') },
    });
    console.log('comment added');
    break;
  }
  case 'state': {
    const [id, name] = pos;
    if (!id || !name) {
      console.error('Usage: state <issueId> <stateName>');
      process.exit(1);
    }
    const issue = await gql(`query($id: String!) { issue(id: $id) { team { id } } }`, { id });
    const s = await resolveState(issue.issue.team.id, name);
    await gql(`mutation($id: String!, $i: IssueUpdateInput!) { issueUpdate(id: $id, input: $i) { success } }`, {
      id,
      i: { stateId: s.id },
    });
    console.log(`moved to ${s.name}`);
    break;
  }
  case 'find': {
    if (!flags['project'] || !flags['title']) {
      console.error('Usage: find --project <name|id> --title <prefix>');
      process.exit(1);
    }
    const p = await resolveProject(flags['project']);
    // List-then-filter instead of searchIssues: Linear's text search does not
    // reliably match bracketed prefixes like "[PR #8]" in titles.
    const d = await gql(
      `query($f: IssueFilter!) { issues(filter: $f, first: 250) { nodes { id title } } }`,
      { f: { project: { id: { eq: p.id } } } },
    );
    const hit = d.issues.nodes.find((n) => n.title.startsWith(flags['title']));
    if (hit) console.log(hit.id);
    break;
  }
  case 'find-key': {
    if (!pos[0]) {
      console.error('Usage: find-key <ISSUE-KEY>');
      process.exit(1);
    }
    const d = await gql(
      `query($q: String!) { searchIssues(term: $q, first: 10) { nodes { id identifier } } }`,
      { q: pos[0] },
    );
    const hit = d.searchIssues.nodes.find(
      (n) => n.identifier.toLowerCase() === pos[0].toLowerCase(),
    );
    if (hit) console.log(hit.id);
    break;
  }
  default:
    console.error('Commands: create, comment, state, find, find-key');
    process.exit(1);
}
