import { QueryClient } from '@tanstack/react-query';
import { AdminKeyNotConfiguredError, AdminKeyRejectedError } from './client';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        // Never retry an auth failure. A rejected key stays rejected, and
        // retrying a 503 fresh-install response just delays the affordance that
        // tells the operator to configure a key on the server.
        if (error instanceof AdminKeyRejectedError || error instanceof AdminKeyNotConfiguredError) {
          return false;
        }
        return failureCount < 2;
      },
    },
    mutations: {
      retry: false,
    },
  },
});
