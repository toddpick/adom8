import { useState } from 'react';

import AuthImage from '../mosaic/images/auth-image.png';
import { validateAppKey } from '../api';
import BrandLogo from './BrandLogo';

export default function LoginGate({ onAuthenticated }) {
  const [appKey, setAppKey] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (event) => {
    event.preventDefault();
    const trimmedKey = appKey.trim();

    if (!trimmedKey) {
      setError('App key is required.');
      return;
    }

    setSubmitting(true);
    setError('');

    try {
      const isValid = await validateAppKey(trimmedKey);
      if (!isValid) {
        setError('Invalid app key');
        return;
      }

      onAuthenticated(trimmedKey);
    } catch (requestError) {
      setError(requestError.message || 'Unable to validate app key');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main className="bg-white">
      <div className="relative md:flex">
        <div className="md:w-1/2">
          <div className="flex min-h-[100dvh] h-full flex-col after:flex-1">
            <div className="flex-1">
              <div className="flex h-16 items-center justify-between px-4 sm:px-6 lg:px-8">
                <div className="flex items-center gap-3">
                  <BrandLogo className="h-11 w-11" />
                  <span className="text-sm font-semibold text-gray-900">ADOm8</span>
                </div>
              </div>
            </div>

            <div className="mx-auto w-full max-w-sm px-4 py-8">
              <h1 className="mb-3 text-3xl font-bold text-gray-800">Dashboard access</h1>
              <p className="mb-8 text-sm text-gray-500">Enter your dashboard app key to continue</p>

              <form onSubmit={handleSubmit}>
                <div className="space-y-4">
                  <div>
                    <label className="mb-1 block text-sm font-medium" htmlFor="app-key">
                      App Key
                    </label>
                    <input
                      id="app-key"
                      className="form-input w-full"
                      type="password"
                      value={appKey}
                      onChange={(event) => setAppKey(event.target.value)}
                      autoComplete="off"
                    />
                  </div>
                </div>
                {error ? (
                  <div className="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
                    {error}
                  </div>
                ) : null}
                <div className="mt-6 flex items-center justify-end">
                  <button
                    type="submit"
                    disabled={submitting}
                    className="btn bg-gray-900 text-gray-100 hover:bg-gray-800 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {submitting ? 'Checking...' : 'Unlock Dashboard'}
                  </button>
                </div>
              </form>
            </div>
          </div>
        </div>

        <div className="absolute right-0 top-0 bottom-0 hidden md:block md:w-1/2" aria-hidden="true">
          <img className="h-full w-full object-cover object-center" src={AuthImage} width="760" height="1024" alt="Authentication" />
        </div>
      </div>
    </main>
  );
}
