import { BrowserRouter, Routes, Route, Navigate, useSearchParams } from 'react-router-dom';
import Login from './pages/Login';
import MfaChallenge from './pages/MfaChallenge';
import PasswordReset from './pages/PasswordReset';
import Preview from './pages/Preview';
import Register from './pages/Register';
import './index.css';

function OAuthError() {
  const [params] = useSearchParams();
  const challenge = params.get('login_challenge') ?? '';
  return (
    <div className="card">
      <h1 className="card-title">Sign-in failed</h1>
      <p className="card-subtitle">The social login could not be completed. Please try again or use another method.</p>
      {challenge && (
        <a href={`/login?login_challenge=${encodeURIComponent(challenge)}`} className="btn" style={{ display: 'block', textAlign: 'center', marginTop: '1rem' }}>
          Back to sign in
        </a>
      )}
    </div>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/mfa" element={<MfaChallenge />} />
        <Route path="/password-reset" element={<PasswordReset />} />
        <Route path="/preview" element={<Preview />} />
        <Route path="/register" element={<Register />} />
        <Route path="/auth/oauth2/error" element={<OAuthError />} />
        <Route path="*" element={<Navigate to="/login" />} />
      </Routes>
    </BrowserRouter>
  );
}
