import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type {
  FilterRule,
  TestFilterRuleRequest,
  TestFilterRuleResponse,
  UpsertFilterRuleRequest,
} from '../../api/types';

const RULES_KEY = ['admin', 'rules'];

export function useRulesQuery() {
  return useQuery({
    queryKey: RULES_KEY,
    queryFn: () => apiFetch<FilterRule[]>('/api/admin/rules'),
  });
}

/**
 * The four writes share one invalidation of the list.
 *
 * Refetching rather than patching the cached array by hand is deliberate: the
 * server assigns ids, normalizes patterns and orders by precedence, so a
 * locally-reconstructed list can disagree with what the next request returns.
 */
function useRulesInvalidation() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: RULES_KEY });
}

export function useCreateRuleMutation() {
  const invalidate = useRulesInvalidation();
  return useMutation({
    mutationFn: (rule: UpsertFilterRuleRequest) =>
      apiFetch<FilterRule>('/api/admin/rules', { method: 'POST', body: JSON.stringify(rule) }),
    onSuccess: invalidate,
  });
}

/**
 * PUT /api/admin/rules/{id} — editing, which the legacy admin-rules.js never
 * wired up at all: it could add, delete, test, import and export, so changing a
 * rule's pattern meant deleting it and re-adding it under a new id. AC9 asks
 * for full CRUD, and the endpoint has always been there.
 */
export function useUpdateRuleMutation() {
  const invalidate = useRulesInvalidation();
  return useMutation({
    mutationFn: ({ id, rule }: { id: number; rule: UpsertFilterRuleRequest }) =>
      apiFetch<FilterRule>(`/api/admin/rules/${id}`, {
        method: 'PUT',
        body: JSON.stringify(rule),
      }),
    onSuccess: invalidate,
  });
}

export function useDeleteRuleMutation() {
  const invalidate = useRulesInvalidation();
  return useMutation({
    // DELETE answers 200 with an empty body (Results.Ok()), not 204, so the
    // response is read and discarded rather than assumed to be content-free.
    mutationFn: (id: number) => apiFetch<void>(`/api/admin/rules/${id}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });
}

export function useTestRuleMutation() {
  return useMutation({
    mutationFn: (request: TestFilterRuleRequest) =>
      apiFetch<TestFilterRuleResponse>('/api/admin/rules/test', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
  });
}
