import { BrowserRouter, Routes, Route, Navigate, useSearchParams } from 'react-router';
import Login from './pages/Login';
import Logout from './pages/Logout';
import MfaChallenge from './pages/MfaChallenge';
import MfaSetup from './pages/MfaSetup';
import PasswordReset from './pages/PasswordReset';
import Preview from './pages/Preview';
import Register from './pages/Register';
import SetPassword from './pages/SetPassword';
import './index.css';

function OAuthError() {
  const [params] = useSearchParams();
  const challenge = params.get('login_challenge') ?? '';
  return (
    <div className="login-center">
      <div className="login-card fade-in">
        <div className="login-logo">
          <div className="brand-mark">R</div>
          <span>RediensIAM</span>
        </div>
        <h1 className="login-title">Sign-in failed.</h1>
        <p className="login-subtitle">The social login could not be completed. Please try again or use another method.</p>
        {challenge && (
          <a href={`/login?login_challenge=${encodeURIComponent(challenge)}`} className="btn btn-primary btn-lg" style={{ marginTop: 24, textDecoration: 'none' }}>
            Back to sign in
          </a>
        )}
      </div>
    </div>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/logout" element={<Logout />} />
        <Route path="/mfa" element={<MfaChallenge />} />
        <Route path="/mfa-setup" element={<MfaSetup />} />
        <Route path="/password-reset" element={<PasswordReset />} />
        <Route path="/preview" element={<Preview />} />
        <Route path="/register" element={<Register />} />
        <Route path="/set-password" element={<SetPassword />} />
        <Route path="/auth/oauth2/error" element={<OAuthError />} />
        <Route path="*" element={<Navigate to="/login" />} />
      </Routes>
    </BrowserRouter>
  );
}
